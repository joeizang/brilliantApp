var json = Brilliant.App.RoslynSpike.Run();
if (args is [var output]) File.WriteAllText(output, json);
else Console.WriteLine(json);
