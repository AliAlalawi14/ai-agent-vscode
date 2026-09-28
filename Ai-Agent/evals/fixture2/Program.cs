using WarehouseApp;

var inventory = new Inventory();
inventory.Receive("SKU-1", 5);
Console.WriteLine(inventory.Count("SKU-1"));
