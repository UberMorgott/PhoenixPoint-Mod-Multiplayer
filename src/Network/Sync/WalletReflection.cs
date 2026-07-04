using System;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Multiplayer.Network.Sync
{
    /// <summary>
    /// Reflection bridge to <c>PhoenixPoint.Common.Core.Wallet</c>. The mod has NO compile-time
    /// game references, so every member is resolved by name and cached.
    ///
    /// Verified against the decompile (2026-06-15, <c>PhoenixPoint.Common.Core.Wallet</c>):
    ///   • read amount: indexer <c>ResourceUnit this[ResourceType] => _resources[type]</c>, then
    ///     <c>ResourceUnit.Value</c> (float). Indexer compiles to <c>get_Item(ResourceType)</c>.
    ///   • apply signed diff: <c>bool Apply(ResourceUnit diff, OperationReason reason)</c> (fires
    ///     <c>ResourcesChanged</c>; clamps negative to zero; no-ops for non-accumulating Production).
    ///   • ctor: <c>ResourceUnit(ResourceType type, float value)</c>.
    ///   • reason: <c>OperationReason.None = 0</c> (benign).
    ///   • event: <c>event ResourcesChangedEventHandler ResourcesChanged</c> with delegate
    ///     <c>void (Wallet, ResourcePack, OperationReason)</c>.
    /// </summary>
    public static class WalletReflection
    {
        private static bool _ready;
        private static Type _resourceType;     // PhoenixPoint.Common.Core.ResourceType (enum)
        private static Type _resourceUnitType; // PhoenixPoint.Common.Core.ResourceUnit (struct)
        private static Type _operationReason;  // PhoenixPoint.Common.Core.OperationReason (enum)
        private static MethodInfo _getItem;    // Wallet.get_Item(ResourceType)
        private static FieldInfo _ruValueField;       // ResourceUnit.Value
        private static ConstructorInfo _ruCtor;       // ResourceUnit(ResourceType, float)
        private static MethodInfo _applyMethod;       // Wallet.Apply(ResourceUnit, OperationReason)
        private static EventInfo _resourcesChangedEvt;// Wallet.ResourcesChanged
        private static object _reasonNone;            // boxed OperationReason.None
        private static bool _warnedNotReady;          // one-shot diag for the silent !_ready no-op path

        /// <summary>DIAG (wallet rail): the silent <c>!_ready</c> no-op path (reflection miss) logged ONCE —
        /// GetAmount/ApplyDiff run 11× per snapshot, so per-call logging would spam. No behavior change.</summary>
        private static void WarnNotReadyOnce(string site)
        {
            if (_warnedNotReady) return;
            _warnedNotReady = true;
            Debug.Log("[Multiplayer] WalletReflection guard=not-ready at " + site
                + " getItem=" + (_getItem != null) + " valueField=" + (_ruValueField != null)
                + " ruCtor=" + (_ruCtor != null) + " apply=" + (_applyMethod != null)
                + " (wallet reads/writes no-op; logged once)");
        }

        private static void Ensure(object wallet)
        {
            if (_ready) return;
            var walletType = wallet != null
                ? wallet.GetType()
                : AccessTools.TypeByName("PhoenixPoint.Common.Core.Wallet");
            if (walletType == null) return;

            _resourceType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceType");
            _resourceUnitType = AccessTools.TypeByName("PhoenixPoint.Common.Core.ResourceUnit");
            _operationReason = AccessTools.TypeByName("PhoenixPoint.Common.Core.OperationReason");
            if (_resourceType == null || _resourceUnitType == null || _operationReason == null) return;

            // Indexer getter: get_Item(ResourceType) -> ResourceUnit.
            _getItem = AccessTools.Method(walletType, "get_Item", new[] { _resourceType });
            _ruValueField = AccessTools.Field(_resourceUnitType, "Value");
            _ruCtor = AccessTools.Constructor(_resourceUnitType, new[] { _resourceType, typeof(float) });
            _applyMethod = AccessTools.Method(walletType, "Apply", new[] { _resourceUnitType, _operationReason });
            _resourcesChangedEvt = walletType.GetEvent("ResourcesChanged",
                BindingFlags.Public | BindingFlags.Instance);
            try { _reasonNone = Enum.ToObject(_operationReason, 0); } catch { _reasonNone = null; }

            _ready = _getItem != null && _ruValueField != null && _ruCtor != null && _applyMethod != null;
        }

        /// <summary>Current amount of a resource (enum int value) in the wallet, or 0 on failure.</summary>
        public static float GetAmount(object wallet, int type)
        {
            if (wallet == null) return 0f;
            try
            {
                Ensure(wallet);
                if (!_ready) { WarnNotReadyOnce("GetAmount"); return 0f; }
                object enumVal = Enum.ToObject(_resourceType, type);
                object unit = _getItem.Invoke(wallet, new[] { enumVal }); // boxed ResourceUnit
                return (float)_ruValueField.GetValue(unit);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] WalletReflection.GetAmount failed: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>Apply a signed diff to a resource slot via <c>Wallet.Apply(ResourceUnit, OperationReason.None)</c>.</summary>
        public static void ApplyDiff(object wallet, int type, float diff)
        {
            if (wallet == null) return;
            try
            {
                Ensure(wallet);
                if (!_ready) { WarnNotReadyOnce("ApplyDiff"); return; }
                object enumVal = Enum.ToObject(_resourceType, type);
                object unit = _ruCtor.Invoke(new[] { enumVal, (object)diff }); // ResourceUnit(type, diff)
                _applyMethod.Invoke(wallet, new[] { unit, _reasonNone });
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] WalletReflection.ApplyDiff failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Subscribe to <c>Wallet.ResourcesChanged</c> and route every fire to <paramref name="onChanged"/>.
        /// The event delegate is <c>void (Wallet, ResourcePack, OperationReason)</c> — the last param is a
        /// value type (enum), so a fully-<c>object</c> adapter is NOT delegate-compatible. We emit a
        /// <see cref="DynamicMethod"/> with the exact delegate signature that ignores its args and calls
        /// the supplied callback. Returns the bound delegate (pass it back to <see cref="UnsubscribeResourcesChanged"/>),
        /// or null on failure.
        /// </summary>
        public static Delegate SubscribeResourcesChanged(object wallet, Action onChanged)
        {
            if (wallet == null || onChanged == null) return null;
            try
            {
                Ensure(wallet);
                // DIAG (wallet rail): distinguish the two silent reflection-miss returns — either kills the
                // ResourcesChanged event path (poll backstop only). Rare (bind-time only). No behavior change.
                if (_resourcesChangedEvt == null)
                {
                    Debug.Log("[Multiplayer] Wallet ResourcesChanged subscribe guard=event-missing (reflection miss)");
                    return null;
                }
                Type handlerType = _resourcesChangedEvt.EventHandlerType;
                MethodInfo invoke = handlerType.GetMethod("Invoke");
                if (invoke == null)
                {
                    Debug.Log("[Multiplayer] Wallet ResourcesChanged subscribe guard=invoke-missing on " + handlerType.Name);
                    return null;
                }

                ParameterInfo[] ps = invoke.GetParameters();
                Type[] sig = new Type[ps.Length];
                for (int i = 0; i < ps.Length; i++) sig[i] = ps[i].ParameterType;

                // DynamicMethod(returns void, params = exact delegate sig). Body: load the captured
                // Action (arg[paramCount]) and call Action.Invoke(); ignore all event args.
                Type[] dmSig = new Type[sig.Length + 1];
                dmSig[0] = typeof(Action);                 // the closed-over callback as first arg
                for (int i = 0; i < sig.Length; i++) dmSig[i + 1] = sig[i];

                var dm = new DynamicMethod("Wallet_ResourcesChanged_Adapter", typeof(void), dmSig,
                    typeof(WalletReflection).Module, skipVisibility: true);
                ILGenerator il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);                  // the Action
                il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod("Invoke"));
                il.Emit(OpCodes.Ret);

                // Bind arg0 (the Action) so the resulting delegate matches the event's exact signature.
                Delegate handler = dm.CreateDelegate(handlerType, onChanged);
                _resourcesChangedEvt.AddEventHandler(wallet, handler);
                return handler;
            }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] WalletReflection.SubscribeResourcesChanged failed: " + ex.Message);
                return null;
            }
        }

        public static void UnsubscribeResourcesChanged(object wallet, Delegate handler)
        {
            if (wallet == null || handler == null || _resourcesChangedEvt == null) return;
            try { _resourcesChangedEvt.RemoveEventHandler(wallet, handler); }
            catch (Exception ex)
            {
                Debug.LogError("[Multiplayer] WalletReflection.UnsubscribeResourcesChanged failed: " + ex.Message);
            }
        }
    }
}
