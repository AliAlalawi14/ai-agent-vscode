namespace WarehouseApp
{
    /// <summary>Stock levels per product SKU.</summary>
    public class Inventory
    {
        private readonly Dictionary<string, int> _stock = new();

        public int Count(string sku) => _stock.GetValueOrDefault(sku);

        public void Receive(string sku, int quantity)
        {
            if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            _stock[sku] = Count(sku) + quantity;
        }
    }
}
