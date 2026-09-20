using System.Reflection;

return Run(args);

static int Run(string[] args)
{
    if (args.Length == 0 || args is ["--help"] or ["-h"])
    {
        Console.Out.WriteLine(
            """
            Foxhole-Data CLI

            Usage:
              foxdata --help
              foxdata --version

            M1 provides only bootstrap commands. stdio RPC is introduced in M10.
            """);

        return 0;
    }

    if (args is ["--version"])
    {
        var version =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        Console.Out.WriteLine(version);
        return 0;
    }

    Console.Error.WriteLine($"Unknown command or option: {string.Join(' ', args)}");
    return 2;
}
