using System.Text;
using SecondBrain.Cli;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var app = new CliApp(Console.Out, Console.Error, TimeProvider.System);
return await app.RunAsync(args);