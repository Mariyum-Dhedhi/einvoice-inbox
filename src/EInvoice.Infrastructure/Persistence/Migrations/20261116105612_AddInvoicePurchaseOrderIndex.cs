using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicePurchaseOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Invoice_PurchaseOrderNumber",
                schema: "dbo",
                table: "Invoice",
                column: "PurchaseOrderNumber")
                .Annotation("SqlServer:Include", new[] { "Id", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoice_PurchaseOrderNumber",
                schema: "dbo",
                table: "Invoice");
        }
    }
}
