using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBuyerRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "dbo",
                table: "AppRole",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[] { new Guid("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1005"), "6f1c2a352f614d4e9a0b1b7a3f0c1005", "Buyer", "BUYER" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "dbo",
                table: "AppRole",
                keyColumn: "Id",
                keyValue: new Guid("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1005"));
        }
    }
}
