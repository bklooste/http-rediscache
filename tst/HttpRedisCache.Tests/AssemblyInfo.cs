// Startup tests set process-wide environment variables (DOTNET_*, *_FILE) to prove the config surface, and a
// stray variable would leak into a host being built by a parallel test. The suite is small; run it serially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
