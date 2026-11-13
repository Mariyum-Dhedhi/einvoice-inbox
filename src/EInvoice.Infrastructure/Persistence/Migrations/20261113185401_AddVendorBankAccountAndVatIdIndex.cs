using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVendorBankAccountAndVatIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VendorBankAccount",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Iban = table.Column<string>(type: "varchar(34)", unicode: false, maxLength: 34, nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorBankAccount", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_VendorBankAccount_Vendor_VendorId",
                        column: x => x.VendorId,
                        principalSchema: "dbo",
                        principalTable: "Vendor",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Vendor_VatId",
                schema: "dbo",
                table: "Vendor",
                column: "VatId",
                unique: true,
                filter: "[VatId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "CX_VendorBankAccount_RowId",
                schema: "dbo",
                table: "VendorBankAccount",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_VendorBankAccount_Vendor_Iban",
                schema: "dbo",
                table: "VendorBankAccount",
                columns: new[] { "VendorId", "Iban" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorBankAccount",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "UX_Vendor_VatId",
                schema: "dbo",
                table: "Vendor");
        }
    }
}
