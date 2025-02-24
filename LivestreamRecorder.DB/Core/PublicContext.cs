﻿using LivestreamRecorder.DB.Models;
using Microsoft.EntityFrameworkCore;
#nullable disable warnings

namespace LivestreamRecorder.DB.Core;

public class PublicContext : DbContext
{
    public DbSet<Video> Videos { get; set; }
    public DbSet<Channel> Channels { get; set; }

    public PublicContext() { }

    public PublicContext(DbContextOptions<PublicContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        #region Videos
        modelBuilder.Entity<Video>()
            .ToContainer("Videos");

        modelBuilder.Entity<Video>()
            .HasNoDiscriminator();

        modelBuilder.Entity<Video>()
            .HasKey(nameof(Video.id));

        modelBuilder.Entity<Video>()
            .HasPartitionKey(o => o.ChannelId);

        modelBuilder.Entity<Video>()
            .UseETagConcurrency();

        modelBuilder.Entity<Video>()
            .HasOne(o => o.Channel)
            .WithMany(o => o.Videos)
            .HasForeignKey(o => o.ChannelId);
        #endregion

        #region Channels
        modelBuilder.Entity<Channel>()
            .ToContainer("Channels");

        modelBuilder.Entity<Channel>()
            .HasNoDiscriminator();

        modelBuilder.Entity<Channel>()
            .HasKey(nameof(Channel.id));

        modelBuilder.Entity<Channel>()
            .HasPartitionKey(o => o.Source);

        modelBuilder.Entity<Channel>()
            .UseETagConcurrency();
        #endregion
    }
}