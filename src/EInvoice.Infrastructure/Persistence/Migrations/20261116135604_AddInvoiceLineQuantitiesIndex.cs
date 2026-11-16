using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceLineQuantitiesIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLine_Quantities",
                schema: "dbo",
                table: "InvoiceLine",
                columns: new[] { "InvoiceId", "LineNumber" })
                .Annotation("SqlServer:Include", new[] { "Quantity", "PurchaseOrderLineNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InvoiceLine_Quantities",
                schema: "dbo",
                table: "InvoiceLine");
        }
    }
}
