namespace ZScript.ZUnit;

using Xunit;

public static class ZsAssert
{
    // Equality — typed variants for type inference + generic fallback
    public static void EqualInt(int expected, int actual) => Assert.Equal(expected, actual);

    public static void EqualFloat(float expected, float actual, float epsilon) =>
        Assert.True(Math.Abs(expected - actual) <= epsilon,
            $"Expected {expected} ± {epsilon}, but got {actual}");

    public static void EqualBool(bool expected, bool actual) => Assert.Equal(expected, actual);

    public static void EqualStr(string expected, string actual) => Assert.Equal(expected, actual);

    public static void EqualObj(object expected, object actual) => Assert.Equal(expected, actual);

    public static void NotEqualObj(object expected, object actual) => Assert.NotEqual(expected, actual);

    // Boolean
    public static void IsTrue(bool value) => Assert.True(value);

    public static void IsFalse(bool value) => Assert.False(value);

    // Failure
    public static void Fail(string message) => Assert.Fail(message);

    // Exception
    public static void Throws(Action action) => Assert.ThrowsAny<Exception>(action);
}
