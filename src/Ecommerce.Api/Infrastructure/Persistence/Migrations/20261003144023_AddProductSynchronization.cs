using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProductSynchronization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Outbox_Orders_OrderId",
                table: "Outbox");

            migrationBuilder.AddColumn<long>(
                name: "Version",
                table: "Products",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Legacy rows must outrank documents written with Elasticsearch's
            // old internal versioning before product events start arriving.
            migrationBuilder.Sql("""
                UPDATE Products
                SET Version = DATEDIFF_BIG(MILLISECOND, '1970-01-01T00:00:00', SYSUTCDATETIME())
                WHERE Version = 0;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "OrderId",
                table: "Outbox",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddForeignKey(
                name: "FK_Outbox_Orders_OrderId",
                table: "Outbox",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Outbox_Orders_OrderId",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Products");

            migrationBuilder.AlterColumn<Guid>(
                name: "OrderId",
                table: "Outbox",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Outbox_Orders_OrderId",
                table: "Outbox",
                column: "OrderId",
                principalTable: "Orders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
