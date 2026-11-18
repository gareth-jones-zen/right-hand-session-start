// right-hand-session-start.exe. Output is Unicode to a console and UTF-8 when redirected (see ConsoleText).
return RightHandSessionStart.Cli.Run(args, new RightHandSessionStart.CliHost { Out = WinBackground.ConsoleText.Out, Err = WinBackground.ConsoleText.Error });
