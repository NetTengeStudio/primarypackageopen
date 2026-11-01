using System;
using System.Reflection;
using System.Threading;

namespace PrimaryPackage.Core.Fault;

public static class FaultClassifier {
    public static Exception Unwrap(Exception error) {
        int guard = 0;
        while (guard++ < 8) {
            if (error is TargetInvocationException { InnerException: { } inner }) { error = inner; continue; }
            if (error is TypeInitializationException { InnerException: { } init }) { error = init; continue; }
            if (error is AggregateException { InnerExceptions.Count: 1 } aggregate) { error = aggregate.InnerExceptions[0]; continue; }
            break;
        }
        return error;
    }

    public static bool IsContainable(Exception error) {
        Exception root = Unwrap(error);
        return root switch {
            null => false,
            OutOfMemoryException => false,
            AccessViolationException => false,
            AppDomainUnloadedException => false,
            BadImageFormatException => false,
            ThreadInterruptedException => false,
            OperationCanceledException => false,
            _ => true
        };
    }
}
