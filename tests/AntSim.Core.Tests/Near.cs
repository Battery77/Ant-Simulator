namespace AntSim.Core.Tests;

/// <summary>float/double 혼합 근사 비교(xUnit 오버로드 모호성 회피). digits = 소수점 이하 자릿수.</summary>
internal static class Near
{
    public static void Equal(double expected, double actual, int digits) => Assert.Equal(expected, actual, digits);
}
