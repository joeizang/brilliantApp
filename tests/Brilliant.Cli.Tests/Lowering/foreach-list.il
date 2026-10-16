.class private auto ansi abstract sealed beforefieldinit Demo
    extends [System.Runtime]System.Object
{
    // Methods
    .method public hidebysig static
        int32 Total (
            class [System.Collections]System.Collections.Generic.List`1<int32> items
        ) cil managed
    {
        // Code size: 50 (0x32)
        .maxstack 2
        .locals init (
            [0] int32,
            [1] valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<int32>,
            [2] int32
        )

        IL_0000: ldc.i4.0
        IL_0001: stloc.0
        IL_0002: ldarg.0
        IL_0003: callvirt instance valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<!0> class [System.Collections]System.Collections.Generic.List`1<int32>::GetEnumerator()
        IL_0008: stloc.1
        .try
        {
            IL_0009: br.s IL_0017
            // loop start (head: IL_0017)
                IL_000b: ldloca.s 1
                IL_000d: call instance !0 valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<int32>::get_Current()
                IL_0012: stloc.2
                IL_0013: ldloc.0
                IL_0014: ldloc.2
                IL_0015: add
                IL_0016: stloc.0

                IL_0017: ldloca.s 1
                IL_0019: call instance bool valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<int32>::MoveNext()
                IL_001e: brtrue.s IL_000b
            // end loop

            IL_0020: leave.s IL_0030
        } // end .try
        finally
        {
            IL_0022: ldloca.s 1
            IL_0024: constrained. valuetype [System.Collections]System.Collections.Generic.List`1/Enumerator<int32>
            IL_002a: callvirt instance void [System.Runtime]System.IDisposable::Dispose()
            IL_002f: endfinally
        } // end handler

        IL_0030: ldloc.0
        IL_0031: ret
    } // end of method Demo::Total

} // end of class Demo
