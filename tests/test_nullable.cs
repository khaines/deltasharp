using System;
using Parquet.Schema;

class Program {
    static void Main() {
        var lf = new ListField("my_list", new DataField("element", DataType.String, true));
        Console.WriteLine($"3-level list element IsNullable: {lf.Item.IsNullable}");
        
        // Wait, Parquet.Net usually represents lists this way. Let's inspect the fields.
        var dataField = lf.Item as DataField;
        if (dataField != null) {
            Console.WriteLine($"3-level DataField IsNullable: {dataField.IsNullable}");
        }
    }
}
