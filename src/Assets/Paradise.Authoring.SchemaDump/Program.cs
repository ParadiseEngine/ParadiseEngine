using Paradise.Authoring.SchemaDump;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Paradise.Authoring.SchemaDump <game-assembly.dll> <output.json>");
    return 2;
}

try
{
    SchemaDumper.Run(args[0], args[1]);
    Console.WriteLine($"[Paradise] Authoring schema: {args[1]}");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"[Paradise] {e.Message}");
    return 1;
}
