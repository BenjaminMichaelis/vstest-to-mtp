namespace Calculator.Tests;

[TestFixture]
public class ArithmeticTests
{
    [Test]
    public void Add_ReturnsSum() => Assert.That(Arithmetic.Add(2, 3), Is.EqualTo(5));

    [Test]
    public void Subtract_ReturnsDifference() => Assert.That(Arithmetic.Subtract(2, 3), Is.EqualTo(-1));

    [Test]
    public void Divide_ByZero_Throws() =>
        Assert.Throws<DivideByZeroException>(() => Arithmetic.Divide(1, 0));

    [TestCase(2, 3, 6)]
    [TestCase(-2, 3, -6)]
    [TestCase(0, 5, 0)]
    public void Multiply_ReturnsProduct(int left, int right, int expected) =>
        Assert.That(Arithmetic.Multiply(left, right), Is.EqualTo(expected));
}
