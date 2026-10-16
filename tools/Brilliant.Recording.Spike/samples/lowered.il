.class private auto ansi '<Module>'
{
} // end of class <Module>

.class public auto ansi abstract sealed beforefieldinit Demo
	extends [System.Runtime]System.Object
{
	.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableContextAttribute::.ctor(uint8) = (
		01 00 01 00 00
	)
	.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
		01 00 00 00 00
	)
	// Nested Types
	.class nested private auto ansi sealed beforefieldinit '<FetchAsync>d__0'
		extends [System.Runtime]System.ValueType
		implements [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine
	{
		.custom instance void [System.Runtime]System.Runtime.CompilerServices.CompilerGeneratedAttribute::.ctor() = (
			01 00 00 00
		)
		.interfaceimpl type [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine
			.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
				01 00 00 00 00
			)

		// Fields
		.field public int32 '<>1__state'
		.field public valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> '<>t__builder'
		.custom instance void [System.Runtime]System.Runtime.CompilerServices.NullableAttribute::.ctor(uint8) = (
			01 00 00 00 00
		)
		.field public int32 n
		.field private valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter '<>u__1'

		// Methods
		.method private final hidebysig newslot virtual 
			instance void MoveNext () cil managed 
		{
			.override method instance void [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine::MoveNext()
			// Method begins at RVA 0x20e4
			// Header size: 12
			// Code size: 153 (0x99)
			.maxstack 3
			.locals init (
				[0] int32,
				[1] int32,
				[2] valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter,
				[3] class [System.Runtime]System.Exception
			)

			IL_0000: ldarg.0
			IL_0001: ldfld int32 Demo/'<FetchAsync>d__0'::'<>1__state'
			IL_0006: stloc.0
			.try
			{
				IL_0007: ldloc.0
				IL_0008: brfalse.s IL_003f

				IL_000a: ldc.i4.1
				IL_000b: call class [System.Runtime]System.Threading.Tasks.Task [System.Runtime]System.Threading.Tasks.Task::Delay(int32)
				IL_0010: callvirt instance valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter [System.Runtime]System.Threading.Tasks.Task::GetAwaiter()
				IL_0015: stloc.2
				IL_0016: ldloca.s 2
				IL_0018: call instance bool [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter::get_IsCompleted()
				IL_001d: brtrue.s IL_005b

				IL_001f: ldarg.0
				IL_0020: ldc.i4.0
				IL_0021: dup
				IL_0022: stloc.0
				IL_0023: stfld int32 Demo/'<FetchAsync>d__0'::'<>1__state'
				IL_0028: ldarg.0
				IL_0029: ldloc.2
				IL_002a: stfld valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter Demo/'<FetchAsync>d__0'::'<>u__1'
				IL_002f: ldarg.0
				IL_0030: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
				IL_0035: ldloca.s 2
				IL_0037: ldarg.0
				IL_0038: call instance void valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::AwaitUnsafeOnCompleted<valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter, valuetype Demo/'<FetchAsync>d__0'>(!!0&, !!1&)
				IL_003d: leave.s IL_0098

				IL_003f: ldarg.0
				IL_0040: ldfld valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter Demo/'<FetchAsync>d__0'::'<>u__1'
				IL_0045: stloc.2
				IL_0046: ldarg.0
				IL_0047: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter Demo/'<FetchAsync>d__0'::'<>u__1'
				IL_004c: initobj [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter
				IL_0052: ldarg.0
				IL_0053: ldc.i4.m1
				IL_0054: dup
				IL_0055: stloc.0
				IL_0056: stfld int32 Demo/'<FetchAsync>d__0'::'<>1__state'

				IL_005b: ldloca.s 2
				IL_005d: call instance void [System.Runtime]System.Runtime.CompilerServices.TaskAwaiter::GetResult()
				IL_0062: ldarg.0
				IL_0063: ldfld int32 Demo/'<FetchAsync>d__0'::n
				IL_0068: ldc.i4.2
				IL_0069: mul
				IL_006a: stloc.1
				IL_006b: leave.s IL_0084
			} // end .try
			catch [System.Runtime]System.Exception
			{
				IL_006d: stloc.3
				IL_006e: ldarg.0
				IL_006f: ldc.i4.s -2
				IL_0071: stfld int32 Demo/'<FetchAsync>d__0'::'<>1__state'
				IL_0076: ldarg.0
				IL_0077: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
				IL_007c: ldloc.3
				IL_007d: call instance void valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::SetException(class [System.Runtime]System.Exception)
				IL_0082: leave.s IL_0098
			} // end handler

			IL_0084: ldarg.0
			IL_0085: ldc.i4.s -2
			IL_0087: stfld int32 Demo/'<FetchAsync>d__0'::'<>1__state'
			IL_008c: ldarg.0
			IL_008d: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
			IL_0092: ldloc.1
			IL_0093: call instance void valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::SetResult(!0)

			IL_0098: ret
		} // end of method '<FetchAsync>d__0'::MoveNext

		.method private final hidebysig newslot virtual 
			instance void SetStateMachine (
				class [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine stateMachine
			) cil managed 
		{
			.custom instance void [System.Runtime]System.Diagnostics.DebuggerHiddenAttribute::.ctor() = (
				01 00 00 00
			)
			.override method instance void [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine::SetStateMachine(class [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine)
			// Method begins at RVA 0x219c
			// Header size: 1
			// Code size: 13 (0xd)
			.maxstack 8

			IL_0000: ldarg.0
			IL_0001: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
			IL_0006: ldarg.1
			IL_0007: call instance void valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::SetStateMachine(class [System.Runtime]System.Runtime.CompilerServices.IAsyncStateMachine)
			IL_000c: ret
		} // end of method '<FetchAsync>d__0'::SetStateMachine

	} // end of class <FetchAsync>d__0


	// Methods
	.method public hidebysig static 
		class [System.Runtime]System.Threading.Tasks.Task`1<int32> FetchAsync (
			int32 n
		) cil managed 
	{
		.custom instance void [System.Runtime]System.Runtime.CompilerServices.AsyncStateMachineAttribute::.ctor(class [System.Runtime]System.Type) = (
			01 00 15 44 65 6d 6f 2b 3c 46 65 74 63 68 41 73
			79 6e 63 3e 64 5f 5f 30 00 00
		)
		// Method begins at RVA 0x2050
		// Header size: 12
		// Code size: 55 (0x37)
		.maxstack 2
		.locals init (
			[0] valuetype Demo/'<FetchAsync>d__0'
		)

		IL_0000: ldloca.s 0
		IL_0002: call valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<!0> valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::Create()
		IL_0007: stfld valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
		IL_000c: ldloca.s 0
		IL_000e: ldarg.0
		IL_000f: stfld int32 Demo/'<FetchAsync>d__0'::n
		IL_0014: ldloca.s 0
		IL_0016: ldc.i4.m1
		IL_0017: stfld int32 Demo/'<FetchAsync>d__0'::'<>1__state'
		IL_001c: ldloca.s 0
		IL_001e: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
		IL_0023: ldloca.s 0
		IL_0025: call instance void valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::Start<valuetype Demo/'<FetchAsync>d__0'>(!!0&)
		IL_002a: ldloca.s 0
		IL_002c: ldflda valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32> Demo/'<FetchAsync>d__0'::'<>t__builder'
		IL_0031: call instance class [System.Runtime]System.Threading.Tasks.Task`1<!0> valuetype [System.Runtime]System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1<int32>::get_Task()
		IL_0036: ret
	} // end of method Demo::FetchAsync

	.method public hidebysig static 
		int32 Total (
			class [System.Collections]System.Collections.Generic.List`1<int32> items
		) cil managed 
	{
		// Method begins at RVA 0x2094
		// Header size: 12
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

