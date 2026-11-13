using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatchResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MatchResult",
                schema: "dbo",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceLineNumber = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrderLineNumber = table.Column<int>(type: "int", nullable: true),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    InvoiceUnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    OrderUnitPrice = table.Column<decimal>(type: "decimal(18,6)", nullable: true),
                    PriceVariancePercent = table.Column<decimal>(type: "decimal(19,6)", nullable: true),
                    PriceVarianceAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    QuantityInvoiced = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    QuantityAvailable = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    QuantityVariance = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    PriceTolerancePercent = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    MatchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MatchResult", x => new { x.InvoiceId, x.InvoiceLineNumber })
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_MatchResult_Outcome", "[Outcome] IN (1, 2, 3, 4, 5)");
                    table.ForeignKey(
                        name: "FK_MatchResult_Invoice_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "dbo",
                        principalTable: "Invoice",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "CX_MatchResult_RowId",
                schema: "dbo",
                table: "MatchResult",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MatchResult",
                schema: "dbo");
        }
    }
}
