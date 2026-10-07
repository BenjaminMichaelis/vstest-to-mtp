namespace Calculator.Tests;

public class ArithmeticTests
{
    [Fact]
    public void Add_ReturnsSum() => Assert.Equal(5, Arithmetic.Add(2, 3));

    [Fact]
    public void Subtract_ReturnsDifference() => Assert.Equal(-1, Arithmetic.Subtract(2, 3));

    [Fact]
    public void Divide_ByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Arithmetic.Divide(1, 0));

    [Theory]
    [InlineData(2, 3, 6)]
    [InlineData(-2, 3, -6)]
    [InlineData(0, 5, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected) =>
        Assert.Equal(expected, Arithmetic.Multiply(left, right));
}
