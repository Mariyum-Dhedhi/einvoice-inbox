using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace EInvoice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "AppRoleClaimId",
                schema: "dbo");

            migrationBuilder.CreateSequence(
                name: "AppUserClaimId",
                schema: "dbo");

            migrationBuilder.CreateTable(
                name: "AppRole",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRole", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "AppUser",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: true),
                    SecurityStamp = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUser", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "AppRoleClaim",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false, defaultValueSql: "NEXT VALUE FOR [dbo].[AppRoleClaimId]"),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRoleClaim", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AppRoleClaim_AppRole_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "dbo",
                        principalTable: "AppRole",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserClaim",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false, defaultValueSql: "NEXT VALUE FOR [dbo].[AppUserClaimId]"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserClaim", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AppUserClaim_AppUser_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserLogin",
                schema: "dbo",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserLogin", x => new { x.LoginProvider, x.ProviderKey })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AppUserLogin_AppUser_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserRole",
                schema: "dbo",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserRole", x => new { x.UserId, x.RoleId })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AppUserRole_AppRole_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "dbo",
                        principalTable: "AppRole",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUserRole_AppUser_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserToken",
                schema: "dbo",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    RowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserToken", x => new { x.UserId, x.LoginProvider, x.Name })
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_AppUserToken_AppUser_UserId",
                        column: x => x.UserId,
                        principalSchema: "dbo",
                        principalTable: "AppUser",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "dbo",
                table: "AppRole",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1001"), "6f1c2a352f614d4e9a0b1b7a3f0c1001", "Clerk", "CLERK" },
                    { new Guid("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1002"), "6f1c2a352f614d4e9a0b1b7a3f0c1002", "Approver", "APPROVER" },
                    { new Guid("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1003"), "6f1c2a352f614d4e9a0b1b7a3f0c1003", "Auditor", "AUDITOR" },
                    { new Guid("6f1c2a35-2f61-4d4e-9a0b-1b7a3f0c1004"), "6f1c2a352f614d4e9a0b1b7a3f0c1004", "Admin", "ADMIN" }
                });

            migrationBuilder.CreateIndex(
                name: "CX_AppRole_RowId",
                schema: "dbo",
                table: "AppRole",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                schema: "dbo",
                table: "AppRole",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "CX_AppRoleClaim_RowId",
                schema: "dbo",
                table: "AppRoleClaim",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AppRoleClaim_RoleId",
                schema: "dbo",
                table: "AppRoleClaim",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "CX_AppUser_RowId",
                schema: "dbo",
                table: "AppUser",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                schema: "dbo",
                table: "AppUser",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                schema: "dbo",
                table: "AppUser",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "CX_AppUserClaim_RowId",
                schema: "dbo",
                table: "AppUserClaim",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserClaim_UserId",
                schema: "dbo",
                table: "AppUserClaim",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "CX_AppUserLogin_RowId",
                schema: "dbo",
                table: "AppUserLogin",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLogin_UserId",
                schema: "dbo",
                table: "AppUserLogin",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "CX_AppUserRole_RowId",
                schema: "dbo",
                table: "AppUserRole",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserRole_RoleId",
                schema: "dbo",
                table: "AppUserRole",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "CX_AppUserToken_RowId",
                schema: "dbo",
                table: "AppUserToken",
                column: "RowId")
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppRoleClaim",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppUserClaim",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppUserLogin",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppUserRole",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppUserToken",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppRole",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "AppUser",
                schema: "dbo");

            migrationBuilder.DropSequence(
                name: "AppRoleClaimId",
                schema: "dbo");

            migrationBuilder.DropSequence(
                name: "AppUserClaimId",
                schema: "dbo");
        }
    }
}
