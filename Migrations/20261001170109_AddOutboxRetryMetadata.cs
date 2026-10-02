using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ecommerce.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxRetryMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "Outbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "DeadLettered",
                table: "Outbox",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastAttemptAt",
                table: "Outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "Outbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptAt",
                table: "Outbox",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "DeadLettered",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "Outbox");
        }
    }
}
