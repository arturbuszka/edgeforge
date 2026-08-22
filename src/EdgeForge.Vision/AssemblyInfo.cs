using System.Runtime.CompilerServices;

// The transposed-tensor decode and NMS are the parts of postprocessing where
// mistakes actually happen, and the only parts testable without a model on disk.
[assembly: InternalsVisibleTo("EdgeForge.Core.Tests")]
