using System.Runtime.CompilerServices;

// Stage-1 rail harness (tools/RailCheck) asserts on the classifier table, which is built from
// internal members (RailField.Fi/.IsWritable, RailMeta.SerializedMembers/.TypeKeyable/.ApplyList).
// Reflection would work but is stringly typed — a rename would silently turn the gate green.
[assembly: InternalsVisibleTo("RailCheck")]

// Stage-2 deterministic simulation harness (tools/RailSim) EXECUTES the production window journal
// (Multiplayer.Network.Sync.WindowJournal) rather than reimplementing it, for the same reason.
[assembly: InternalsVisibleTo("RailSim")]
