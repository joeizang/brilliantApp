using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: AssemblyVersion("0.0.0.0")]
[module: RefSafetyRules(11)]
[NullableContext(1)]
[Nullable(0)]
public static class Demo
{
	[StructLayout(LayoutKind.Auto)]
	[CompilerGenerated]
	private struct <FetchAsync>d__0 : IAsyncStateMachine
	{
		public int <>1__state;

		[Nullable(0)]
		public AsyncTaskMethodBuilder<int> <>t__builder;

		public int n;

		private TaskAwaiter <>u__1;

		private void MoveNext()
		{
			int num = <>1__state;
			int result;
			try
			{
				TaskAwaiter awaiter;
				if (num != 0)
				{
					awaiter = Task.Delay(1).GetAwaiter();
					if (!awaiter.IsCompleted)
					{
						num = (<>1__state = 0);
						<>u__1 = awaiter;
						<>t__builder.AwaitUnsafeOnCompleted(ref awaiter, ref this);
						return;
					}
				}
				else
				{
					awaiter = <>u__1;
					<>u__1 = default(TaskAwaiter);
					num = (<>1__state = -1);
				}
				awaiter.GetResult();
				result = n * 2;
			}
			catch (Exception exception)
			{
				<>1__state = -2;
				<>t__builder.SetException(exception);
				return;
			}
			<>1__state = -2;
			<>t__builder.SetResult(result);
		}

		void IAsyncStateMachine.MoveNext()
		{
			//ILSpy generated this explicit interface implementation from .override directive in MoveNext
			this.MoveNext();
		}

		[DebuggerHidden]
		private void SetStateMachine(IAsyncStateMachine stateMachine)
		{
			<>t__builder.SetStateMachine(stateMachine);
		}

		void IAsyncStateMachine.SetStateMachine(IAsyncStateMachine stateMachine)
		{
			//ILSpy generated this explicit interface implementation from .override directive in SetStateMachine
			this.SetStateMachine(stateMachine);
		}
	}

	[AsyncStateMachine(typeof(<FetchAsync>d__0))]
	public static Task<int> FetchAsync(int n)
	{
		<FetchAsync>d__0 stateMachine = default(<FetchAsync>d__0);
		stateMachine.<>t__builder = AsyncTaskMethodBuilder<int>.Create();
		stateMachine.n = n;
		stateMachine.<>1__state = -1;
		stateMachine.<>t__builder.Start(ref stateMachine);
		return stateMachine.<>t__builder.Task;
	}

	public static int Total(List<int> items)
	{
		int num = 0;
		List<int>.Enumerator enumerator = items.GetEnumerator();
		try
		{
			while (enumerator.MoveNext())
			{
				int current = enumerator.Current;
				num += current;
			}
			return num;
		}
		finally
		{
			((IDisposable)enumerator/*cast due to constrained. prefix*/).Dispose();
		}
	}
}
