using System;
using Cake.Core;
using Cake.Frosting;

namespace Build.Tasks;

public abstract class FrostingTaskBase<T> : FrostingTask<T> where T : ICakeContext
{
    public override void OnError(Exception exception, T context)
    {
        base.OnError(exception, context);
        Console.WriteLine(exception);
        throw exception;
    }
}