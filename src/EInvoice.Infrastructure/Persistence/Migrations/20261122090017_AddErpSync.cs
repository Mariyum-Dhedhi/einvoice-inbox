using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddErpSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErpId",
                schema: "dbo",
                table: "Vendor",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "dbo",
                table: "Vendor",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "ErpId",
                schema: "dbo",
                table: "PurchaseOrder",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "dbo",
                table: "PurchaseOrder",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "dbo",
                table: "PurchaseOrder",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "ErpId",
                schema: "dbo",
                table: "GoodsReceipt",
                type: "varchar(50)",
                unicode: false,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "dbo",
                table: "GoodsReceipt",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "SyncFailure",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Entity = table.Column<int>(type: "int", nullable: false),
                    ErpId = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FirstSeen = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Tries = table.Column<int>(type: "int", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncFailure", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_SyncFailure_Entity", "[Entity] IN (1, 2, 3)");
                });

            migrationBuilder.CreateTable(
                name: "SyncWatermark",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Entity = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncWatermark", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_SyncWatermark_Entity", "[Entity] IN (1, 2, 3)");
                });

            migrationBuilder.CreateIndex(
                name: "UX_Vendor_ErpId",
                schema: "dbo",
                table: "Vendor",
                column: "ErpId",
                unique: true,
                filter: "[ErpId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Vendor_Source",
                schema: "dbo",
                table: "Vendor",
                sql: "[Source] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "UX_PurchaseOrder_ErpId",
                schema: "dbo",
                table: "PurchaseOrder",
                column: "ErpId",
                unique: true,
                filter: "[ErpId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseOrder_Source",
                schema: "dbo",
                table: "PurchaseOrder",
                sql: "[Source] IN (1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_PurchaseOrder_Status",
                schema: "dbo",
                table: "PurchaseOrder",
                sql: "[Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "UX_GoodsReceipt_ErpId",
                schema: "dbo",
                table: "GoodsReceipt",
                column: "ErpId",
                unique: true,
                filter: "[ErpId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GoodsReceipt_Source",
                schema: "dbo",
                table: "GoodsReceipt",
                sql: "[Source] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "CX_SyncFailure_RowId",
                schema: "dbo",
                table: "SyncFailure",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_SyncFailure_Entity_ErpId",
                schema: "dbo",
                table: "SyncFailure",
                columns: new[] { "Entity", "ErpId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "CX_SyncWatermark_RowId",
                schema: "dbo",
                table: "SyncWatermark",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_SyncWatermark_Entity",
                schema: "dbo",
                table: "SyncWatermark",
                column: "Entity",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncFailure",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "SyncWatermark",
                schema: "dbo");

            migrationBuilder.DropIndex(
                name: "UX_Vendor_ErpId",
                schema: "dbo",
                table: "Vendor");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Vendor_Source",
                schema: "dbo",
                table: "Vendor");

            migrationBuilder.DropIndex(
                name: "UX_PurchaseOrder_ErpId",
                schema: "dbo",
                table: "PurchaseOrder");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseOrder_Source",
                schema: "dbo",
                table: "PurchaseOrder");

            migrationBuilder.DropCheckConstraint(
                name: "CK_PurchaseOrder_Status",
                schema: "dbo",
                table: "PurchaseOrder");

            migrationBuilder.DropIndex(
                name: "UX_GoodsReceipt_ErpId",
                schema: "dbo",
                table: "GoodsReceipt");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GoodsReceipt_Source",
                schema: "dbo",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "ErpId",
                schema: "dbo",
                table: "Vendor");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "dbo",
                table: "Vendor");

            migrationBuilder.DropColumn(
                name: "ErpId",
                schema: "dbo",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "dbo",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "dbo",
                table: "PurchaseOrder");

            migrationBuilder.DropColumn(
                name: "ErpId",
                schema: "dbo",
                table: "GoodsReceipt");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "dbo",
                table: "GoodsReceipt");
        }
    }
}
