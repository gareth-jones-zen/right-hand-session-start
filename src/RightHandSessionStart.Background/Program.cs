// right-hand-session-start-background.exe: no window, ever. What the Claude Code SessionStart hook runs (`hook`): it
// reads the hook's JSON on standard input and writes the briefing to standard output as UTF-8, for Claude Code.
using System.Text;
using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
return RightHandSessionStart.Cli.Run(args, new RightHandSessionStart.CliHost { Out = output, Err = TextWriter.Null, In = input });
