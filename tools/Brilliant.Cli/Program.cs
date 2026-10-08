using Brilliant.Cli;

return Run(args, Console.Out, Console.Error);

static int Run(string[] args, TextWriter output, TextWriter error)
{
    if (args is not [var command, var contentRoot, ..])
        return Usage(error);

    switch (command)
    {
        case "validate" when args.Length == 2:
            return Report(ContentValidator.Validate(contentRoot), contentRoot, output, error, "valid");

        case "pack" when args is [_, _, "-o" or "--output", var outPath]:
            var report = ContentPacker.Pack(contentRoot, outPath);
            return Report(report, contentRoot, output, error, $"packed to {outPath}");

        default:
            return Usage(error);
    }
}

static int Report(ValidationReport report, string root, TextWriter output, TextWriter error, string success)
{
    if (report.IsValid)
    {
        output.WriteLine($"OK: {root} {success}");
        return 0;
    }
    foreach (var e in report.Errors) error.WriteLine($"error: {e}");
    error.WriteLine($"{report.Errors.Count} error(s); nothing was published.");
    return 1;
}

static int Usage(TextWriter error)
{
    error.WriteLine("usage: brilliant validate <contentRoot>");
    error.WriteLine("       brilliant pack <contentRoot> -o <pack.zip>");
    return 2;
}
