using System.Runtime.CompilerServices;

// Stage-1 rail harness (tools/RailCheck) asserts on the classifier table, which is built from
// internal members (RailField.Fi/.IsWritable, RailMeta.SerializedMembers/.TypeKeyable/.ApplyList).
// Reflection would work but is stringly typed — a rename would silently turn the gate green.
[assembly: InternalsVisibleTo("RailCheck")]
