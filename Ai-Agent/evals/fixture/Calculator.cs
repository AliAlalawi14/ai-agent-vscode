namespace EvalShop
{
    /// <summary>Math helpers used by CalculatorController.</summary>
    public class Calculator
    {
        /// <summary>Adds two integers; throws OverflowException on overflow.</summary>
        public int Add(int a, int b)
        {
            checked
            {
                return a + b;
            }
        }

        /// <summary>Integer division; throws DivideByZeroException when b is 0.</summary>
        public int Divide(int a, int b)
        {
            if (b == 0)
                throw new DivideByZeroException("Cannot divide by zero.");
            return a / b;
        }

        /// <summary>Returns n! for n &gt;= 0; throws ArgumentException for negative n.</summary>
        public long Factorial(int n)
        {
            if (n < 0)
                throw new ArgumentException("Factorial is not defined for negative numbers.");
            long result = 1;
            checked
            {
                for (var i = 2; i <= n; i++)
                    result *= i;
            }
            return result;
        }

        /// <summary>Returns the square of x.</summary>
        public int Square(int x)
        {
            checked
            {
                return x * x;
            }
        }
    }
}
