using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBankAccountTwoPersonRule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterTable(
                name: "VendorBankAccount",
                schema: "dbo")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "VendorBankAccountHistory")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "dbo")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConfirmedAt",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConfirmedBy",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProposedAt",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "datetimeoffset",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<string>(
                name: "ProposedBy",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidFrom",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified))
                .Annotation("SqlServer:TemporalIsPeriodStartColumn", true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidTo",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified))
                .Annotation("SqlServer:TemporalIsPeriodEndColumn", true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                schema: "dbo",
                table: "VendorBankAccount",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddCheckConstraint(
                name: "CK_VendorBankAccount_Status",
                schema: "dbo",
                table: "VendorBankAccount",
                sql: "[Status] IN (1, 2)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_VendorBankAccount_Status",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.DropColumn(
                name: "ConfirmedBy",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.DropColumn(
                name: "ProposedAt",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.DropColumn(
                name: "ProposedBy",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                schema: "dbo",
                table: "VendorBankAccount")
                .Annotation("SqlServer:TemporalIsPeriodStartColumn", true);

            migrationBuilder.DropColumn(
                name: "ValidTo",
                schema: "dbo",
                table: "VendorBankAccount")
                .Annotation("SqlServer:TemporalIsPeriodEndColumn", true);

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "dbo",
                table: "VendorBankAccount");

            migrationBuilder.AlterTable(
                name: "VendorBankAccount",
                schema: "dbo")
                .OldAnnotation("SqlServer:IsTemporal", true)
                .OldAnnotation("SqlServer:TemporalHistoryTableName", "VendorBankAccountHistory")
                .OldAnnotation("SqlServer:TemporalHistoryTableSchema", "dbo")
                .OldAnnotation("SqlServer:TemporalPeriodEndColumnName", "ValidTo")
                .OldAnnotation("SqlServer:TemporalPeriodStartColumnName", "ValidFrom");
        }
    }
}
