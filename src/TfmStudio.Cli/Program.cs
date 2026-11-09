using System.Globalization;
using TfmStudio.Cli;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

return args.FirstOrDefault() switch
{
    "check" => Commands.Check(),
    "bench" => Commands.Bench(args.Skip(1).ToArray()),
    "figures" => Commands.Figures(args.Length > 1 ? args[1] : "docs/figures"),
    "scan" => await Commands.ScanAsync(args.Skip(1).ToArray()),
    _ => Commands.Usage(),
};
