.class private auto ansi abstract sealed beforefieldinit Demo
    extends [System.Runtime]System.Object
{
    // Nested Types
    .class nested private auto ansi sealed beforefieldinit '<>c__DisplayClass0_0'
        extends [System.Runtime]System.Object
    {
        .custom instance void [System.Runtime]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = (
            01 00 00 00
        )
        // Fields
        .field public int32 count

        // Methods
        .method public hidebysig specialname rtspecialname
            instance void .ctor () cil managed
        {
            // Code size: 7 (0x7)
            .maxstack 8

            IL_0000: ldarg.0
            IL_0001: call instance void [System.Runtime]System.Object::.ctor()
            IL_0006: ret
        } // end of method '<>c__DisplayClass0_0'::.ctor

        .method assembly hidebysig
            instance int32 '<Counter>b__0' () cil managed
        {
            // Code size: 18 (0x12)
            .maxstack 3
            .locals init (
                [0] int32
            )

            IL_0000: ldarg.0
            IL_0001: ldfld int32 Demo/'<>c__DisplayClass0_0'::count
            IL_0006: stloc.0
            IL_0007: ldarg.0
            IL_0008: ldloc.0
            IL_0009: ldc.i4.1
            IL_000a: add
            IL_000b: stfld int32 Demo/'<>c__DisplayClass0_0'::count
            IL_0010: ldloc.0
            IL_0011: ret
        } // end of method '<>c__DisplayClass0_0'::'<Counter>b__0'

    } // end of class <>c__DisplayClass0_0

    // Methods
    .method public hidebysig static
        class [System.Runtime]System.Func`1<int32> Counter (
            int32 start
        ) cil managed
    {
        // Code size: 24 (0x18)
        .maxstack 8

        IL_0000: newobj instance void Demo/'<>c__DisplayClass0_0'::.ctor()
        IL_0005: dup
        IL_0006: ldarg.0
        IL_0007: stfld int32 Demo/'<>c__DisplayClass0_0'::count
        IL_000c: ldftn instance int32 Demo/'<>c__DisplayClass0_0'::'<Counter>b__0'()
        IL_0012: newobj instance void class [System.Runtime]System.Func`1<int32>::.ctor(object, native int)
        IL_0017: ret
    } // end of method Demo::Counter

} // end of class Demo
