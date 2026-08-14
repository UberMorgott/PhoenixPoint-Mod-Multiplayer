using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace Multiplayer.Transport
{
    /// <summary>
    /// Steam profile pictures for roster rows, behind the same Facepunch boundary everything else Steam
    /// lives behind. The UI never names a Steamworks type — it asks <see cref="SteamProbe.Avatar"/> for a
    /// <see cref="Sprite"/> and gets null until one exists.
    ///
    /// ONE ENTRY PER ID, FOREVER. The dictionary is seeded with null the moment a fetch starts, so a
    /// per-frame paint (which is exactly how the roster repaints — see UiToolkit.PaintAvatar) asks for the
    /// same id sixty times a second and starts exactly one download. A fetch that fails leaves the null in
    /// place, which is also the "do not ask again" marker: a friend with no avatar must not become a
    /// per-frame Steam call.
    ///
    /// internal, and no Facepunch-typed FIELD anywhere in it, for the reason SteamInvite's own header
    /// spells out: only a type the CLR can load without Facepunch.Steamworks.Win64 may be named from a
    /// guarded call site, and SteamProbe's NoInlining shim is what puts the JIT failure inside a try.
    /// </summary>
    internal static class SteamAvatars
    {
        private static readonly Dictionary<ulong, Sprite> Cache = new Dictionary<ulong, Sprite>();

        /// <summary>The avatar for <paramref name="steamId"/>, or null while it is still downloading (and
        /// permanently, for an id Steam has no picture for). Never blocks.</summary>
        public static Sprite Get(ulong steamId)
        {
            if (steamId == 0) return null;
            Sprite sprite;
            if (Cache.TryGetValue(steamId, out sprite)) return sprite;
            Cache[steamId] = null;   // claim the id BEFORE the await, or every frame starts a new fetch
            Fetch(steamId);
            return null;
        }

        /// <summary>async void on purpose: fire-and-forget, exactly like SteamInvite.HostPublish. The
        /// result lands in the cache and the next frame's paint picks it up — nothing waits on it.</summary>
        private static async void Fetch(ulong steamId)
        {
            try
            {
                if (!SteamClient.IsValid) return;
                // Medium is 64x64 — the largest size that is still smaller than the cell any of the three
                // roster surfaces gives it, so nothing is ever upscaled and Large's 184x184 download is
                // never paid for.
                var image = await SteamFriends.GetMediumAvatarAsync(steamId);
                if (!image.HasValue) return;
                Cache[steamId] = ToSprite(image.Value);
            }
            catch (Exception ex)
            {
                MpLog.LogWarning("[Multiplayer] steam avatar fetch failed for " + steamId + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Steam's raw RGBA buffer as a uGUI sprite. THE ROWS ARE REVERSED, and that is not cosmetic:
        /// Steam hands the image top row first, Unity's texture memory is bottom row first, so a straight
        /// copy draws every avatar upside down.
        /// </summary>
        private static Sprite ToSprite(Steamworks.Data.Image image)
        {
            int w = (int)image.Width, h = (int)image.Height;
            var data = image.Data;
            if (w <= 0 || h <= 0 || data == null || data.Length < w * h * 4) return null;

            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int src = y * w * 4, dst = (h - 1 - y) * w;
                for (int x = 0; x < w; x++, src += 4)
                    pixels[dst + x] = new Color32(data[src], data[src + 1], data[src + 2], data[src + 3]);
            }

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f));
        }
    }
}
