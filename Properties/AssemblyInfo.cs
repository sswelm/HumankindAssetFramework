// Expose internal members (registry parse/resolve/match helpers) to the test project.
// The plugin ships no tests itself; this only widens visibility to the sibling test assembly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HumankindAssetFramework.Tests")]
