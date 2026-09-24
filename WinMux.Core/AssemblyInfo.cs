using System.Runtime.CompilerServices;

// The connection sources decode other tools' stored passwords, and those routines are the part most
// worth checking against the format rather than against themselves. They are internal because
// nothing outside Core should call them.
[assembly: InternalsVisibleTo("WinMux.Tests")]
