.class private auto ansi beforefieldinit Program
    extends [System.Runtime]System.Object
{
    .custom instance void [System.Runtime]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = (
        01 00 00 00
    )
    // Methods
    .method private hidebysig static
        void '<Main>$' (
            string[] args
        ) cil managed
    {
        // Code size: 49 (0x31)
        .maxstack 3
        .entrypoint
        .locals init (
            [0] int32,
            [1] int32[],
            [2] int32,
            [3] int32
        )

        IL_0000: ldc.i4.0
        IL_0001: stloc.0
        IL_0002: ldc.i4.3
        IL_0003: newarr [System.Runtime]System.Int32
        IL_0008: dup
        IL_0009: ldtoken field valuetype '<PrivateImplementationDetails>'/'__StaticArrayInitTypeSize=12' '<PrivateImplementationDetails>'::'4636993D3E1DA4E9D6B8F87B79E8F7C6D018580D52661950EABC3845C5897A4D'
        IL_000e: call void [System.Runtime]System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(class [System.Runtime]System.Array, valuetype [System.Runtime]System.RuntimeFieldHandle)
        IL_0013: stloc.1
        IL_0014: ldc.i4.0
        IL_0015: stloc.2
        IL_0016: br.s IL_0024
        // loop start (head: IL_0024)
            IL_0018: ldloc.1
            IL_0019: ldloc.2
            IL_001a: ldelem.i4
            IL_001b: stloc.3
            IL_001c: ldloc.0
            IL_001d: ldloc.3
            IL_001e: add
            IL_001f: stloc.0
            IL_0020: ldloc.2
            IL_0021: ldc.i4.1
            IL_0022: add
            IL_0023: stloc.2

            IL_0024: ldloc.2
            IL_0025: ldloc.1
            IL_0026: ldlen
            IL_0027: conv.i4
            IL_0028: blt.s IL_0018
        // end loop

        IL_002a: ldloc.0
        IL_002b: call void [System.Console]System.Console::WriteLine(int32)
        IL_0030: ret
    } // end of method Program::'<Main>$'

    .method public hidebysig specialname rtspecialname
        instance void .ctor () cil managed
    {
        // Code size: 7 (0x7)
        .maxstack 8

        IL_0000: ldarg.0
        IL_0001: call instance void [System.Runtime]System.Object::.ctor()
        IL_0006: ret
    } // end of method Program::.ctor

} // end of class Program
