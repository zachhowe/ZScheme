namespace ZScript.Runtime;

public abstract record TailCall<T>
{
    public sealed record Done(T Value) : TailCall<T>;
    public sealed record More(Func<TailCall<T>> Next) : TailCall<T>;

    public static T Run(TailCall<T> initial)
    {
        var current = initial;
        while (true)
        {
            switch (current)
            {
                case Done done:
                    return done.Value;
                case More more:
                    current = more.Next();
                    break;
            }
        }
    }
}
