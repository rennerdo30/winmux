using System.Runtime.CompilerServices;

// The sources decode other tools' stored passwords, and those routines are the part most worth
// checking against the format rather than against themselves. Internal, because nothing outside this
// assembly has any business calling them.
[assembly: InternalsVisibleTo("WinMux.Tests")]
