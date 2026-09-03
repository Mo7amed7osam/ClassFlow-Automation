using Xunit;

// WPF fixtures share process-wide ConsoleLogger subscribers and dispatcher-affine
// collections. xUnit's parallel SynchronizationContext is not a WPF Dispatcher.
// Serialize this UI test assembly; production logging/engine behavior is unchanged.
[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
