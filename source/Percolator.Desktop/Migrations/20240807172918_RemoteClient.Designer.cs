﻿// <auto-generated />
using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Percolator.Desktop.Data;

#nullable disable

namespace Percolator.Desktop.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20240807172918_RemoteClient")]
    partial class RemoteClient
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "8.0.7");

            modelBuilder.Entity("Percolator.Desktop.Data.RemoteClient", b =>
                {
                    b.Property<int?>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Identity")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("RemoteClient");
                });

            modelBuilder.Entity("Percolator.Desktop.Data.RemoteClientIp", b =>
                {
                    b.Property<int?>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("IpAddress")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<int>("RemoteClientId")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("RemoteClientId");

                    b.ToTable("RemoteClientIps");
                });

            modelBuilder.Entity("Percolator.Desktop.Data.RemoteClientIp", b =>
                {
                    b.HasOne("Percolator.Desktop.Data.RemoteClient", "RemoteClient")
                        .WithMany("RemoteClientIps")
                        .HasForeignKey("RemoteClientId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("RemoteClient");
                });

            modelBuilder.Entity("Percolator.Desktop.Data.RemoteClient", b =>
                {
                    b.Navigation("RemoteClientIps");
                });
#pragma warning restore 612, 618
        }
    }
}
