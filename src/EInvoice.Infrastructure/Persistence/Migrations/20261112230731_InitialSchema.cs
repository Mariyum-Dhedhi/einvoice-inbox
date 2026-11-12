using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateTable(
                name: "Vendor",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VatId = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendor", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Invoice",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceFormat = table.Column<int>(type: "int", nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IssueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PurchaseOrderNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GrossTotal_Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    GrossTotal_Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    NetTotal_Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    NetTotal_Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true),
                    TaxTotal_Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    TaxTotal_Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoice", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_Invoice_SourceFormat", "[SourceFormat] IN (1, 2, 3)");
                    table.CheckConstraint("CK_Invoice_Status", "[Status] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
                    table.ForeignKey(
                        name: "FK_Invoice_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalSchema: "dbo",
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrder",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrder", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PurchaseOrder_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalSchema: "dbo",
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLine",
                schema: "dbo",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    PurchaseOrderLineNumber = table.Column<int>(type: "int", nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NetAmount_Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetAmount_Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLine", x => new { x.InvoiceId, x.LineNumber })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_InvoiceLine_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dbo",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceStatusChange",
                schema: "dbo",
                columns: table => new
                {
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: true),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceStatusChange", x => new { x.InvoiceId, x.Sequence })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_InvoiceStatusChange_FromStatus", "[FromStatus] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
                    table.CheckConstraint("CK_InvoiceStatusChange_ToStatus", "[ToStatus] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
                    table.ForeignKey(
                        name: "FK_InvoiceStatusChange_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dbo",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceipt",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReceivedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceipt", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_GoodsReceipt_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalSchema: "dbo",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLine",
                schema: "dbo",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLine", x => new { x.PurchaseOrderId, x.LineNumber })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLine_PurchaseOrder_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalSchema: "dbo",
                        principalTable: "PurchaseOrder",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GoodsReceiptLine",
                schema: "dbo",
                columns: table => new
                {
                    PurchaseOrderLineNumber = table.Column<int>(type: "int", nullable: false),
                    GoodsReceiptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuantityReceived = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoodsReceiptLine", x => new { x.GoodsReceiptId, x.PurchaseOrderLineNumber })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_GoodsReceiptLine_GoodsReceipt_GoodsReceiptId",
                        column: x => x.GoodsReceiptId,
                        principalSchema: "dbo",
                        principalTable: "GoodsReceipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "CX_GoodsReceipt_RowId",
                schema: "dbo",
                table: "GoodsReceipt",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_GoodsReceipt_PurchaseOrderId",
                schema: "dbo",
                table: "GoodsReceipt",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceipt_Number",
                schema: "dbo",
                table: "GoodsReceipt",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CX_GoodsReceiptLine_RowId",
                schema: "dbo",
                table: "GoodsReceiptLine",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "CX_Invoice_RowId",
                schema: "dbo",
                table: "Invoice",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_Status",
                schema: "dbo",
                table: "Invoice",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_Invoice_Vendor_InvoiceNumber",
                schema: "dbo",
                table: "Invoice",
                columns: new[] { "VendorId", "InvoiceNumber" },
                unique: true,
                filter: "[VendorId] IS NOT NULL AND [InvoiceNumber] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "CX_InvoiceLine_RowId",
                schema: "dbo",
                table: "InvoiceLine",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "CX_InvoiceStatusChange_RowId",
                schema: "dbo",
                table: "InvoiceStatusChange",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "CX_PurchaseOrder_RowId",
                schema: "dbo",
                table: "PurchaseOrder",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrder_VendorId",
                schema: "dbo",
                table: "PurchaseOrder",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrder_Number",
                schema: "dbo",
                table: "PurchaseOrder",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CX_PurchaseOrderLine_RowId",
                schema: "dbo",
                table: "PurchaseOrderLine",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "CX_Vendor_RowId",
                schema: "dbo",
                table: "Vendor",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_Vendor_Number",
                schema: "dbo",
                table: "Vendor",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoodsReceiptLine",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "InvoiceLine",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "InvoiceStatusChange",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLine",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "GoodsReceipt",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Invoice",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "PurchaseOrder",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Vendor",
                schema: "dbo");
        }
    }
}
