using Microsoft.AspNetCore.Mvc;

namespace EvalShop.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class CalculatorController : ControllerBase
    {
        private readonly Calculator _calculator;

        public CalculatorController(Calculator calculator)
        {
            _calculator = calculator;
        }

        [HttpGet("add")]
        public ActionResult<int> Add(int a, int b) => _calculator.Add(a, b);

        [HttpGet("divide")]
        public ActionResult<int> Divide(int a, int b)
        {
            try
            {
                return _calculator.Divide(a, b);
            }
            catch (DivideByZeroException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("factorial")]
        public ActionResult<long> Factorial(int n)
        {
            try
            {
                return _calculator.Factorial(n);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }
    }
}
