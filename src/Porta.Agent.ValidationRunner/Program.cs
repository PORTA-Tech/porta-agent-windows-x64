using System.Diagnostics;
using Porta.Agent.Common.Control;

long frequency = Stopwatch.Frequency;
if (args.Length > 0 && long.TryParse(args[0], out long parsed) && parsed > 0)
{
    frequency = parsed;
}

IReadOnlyList<ControlValidation.ValidationResult> results = ControlValidation.RunBasicSuite(frequency);
int failed = 0;

Console.WriteLine($"Control validation frequency={frequency}");
foreach (ControlValidation.ValidationResult result in results)
{
    string status = result.IsPass ? "PASS" : "FAIL";
    Console.WriteLine($"{status} {result.Name} {result.Detail}");
    if (!result.IsPass)
    {
        failed++;
    }
}

Console.WriteLine($"Summary: total={results.Count} failed={failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;
