namespace Calculator.Tests;

[TestClass]
public sealed class ArithmeticTests
{
    [TestMethod]
    public void Add_ReturnsSum() => Assert.AreEqual(5, Arithmetic.Add(2, 3));

    [TestMethod]
    public void Subtract_ReturnsDifference() => Assert.AreEqual(-1, Arithmetic.Subtract(2, 3));

    [TestMethod]
    public void Divide_ByZero_Throws() =>
        Assert.ThrowsExactly<DivideByZeroException>(() => Arithmetic.Divide(1, 0));

    [TestMethod]
    [DataRow(2, 3, 6)]
    [DataRow(-2, 3, -6)]
    [DataRow(0, 5, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected) =>
        Assert.AreEqual(expected, Arithmetic.Multiply(left, right));
}
