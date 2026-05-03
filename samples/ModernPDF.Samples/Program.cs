using ModernPDF.Samples.Commands;
using System.CommandLine;

RootCommand rootCommand = new("ModernPDF sample commands.");
rootCommand.Add(CreateSampleCommand.Build());
rootCommand.Add(ExtractSampleCommand.Build());
rootCommand.Add(RedactSampleCommand.Build());
rootCommand.Add(SecureSampleCommand.Build());

ParseResult parseResult = rootCommand.Parse(args);
return parseResult.Invoke(new InvocationConfiguration());
