.class private auto ansi abstract sealed beforefieldinit Demo
    extends [System.Runtime]System.Object
{
    // Methods
    .method public hidebysig static
        string FirstLine (
            string path
        ) cil managed
    {
        // Code size: 37 (0x25)
        .maxstack 2
        .locals init (
            [0] class [System.Runtime]System.IO.StreamReader,
            [1] string
        )

        IL_0000: ldarg.0
        IL_0001: newobj instance void [System.Runtime]System.IO.StreamReader::.ctor(string)
        IL_0006: stloc.0
        .try
        {
            IL_0007: ldloc.0
            IL_0008: callvirt instance string [System.Runtime]System.IO.TextReader::ReadLine()
            IL_000d: dup
            IL_000e: brtrue.s IL_0016

            IL_0010: pop
            IL_0011: ldstr ""

            IL_0016: stloc.1
            IL_0017: leave.s IL_0023
        } // end .try
        finally
        {
            IL_0019: ldloc.0
            IL_001a: brfalse.s IL_0022

            IL_001c: ldloc.0
            IL_001d: callvirt instance void [System.Runtime]System.IDisposable::Dispose()

            IL_0022: endfinally
        } // end handler

        IL_0023: ldloc.1
        IL_0024: ret
    } // end of method Demo::FirstLine

} // end of class Demo
