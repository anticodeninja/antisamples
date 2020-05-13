// This Source Code Form is subject to the terms of the
// Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
// with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright 2020 Artem Yamshanov, me [at] anticode.ninja

﻿namespace InfiniteLists
{
    using System;
    using System.Data;
    using System.Data.SqlClient;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using AntiFramework.Utils;
    using Newtonsoft.Json;
    using WebSocketSharp.Server;

    public class Program
    {
        #region Constants

        private const int SHOW_STAT_EVERY = 10000;

        private const int SHOW_STAT = 1;

        private const int SHOW_ITEM = 2;

        #endregion Constants

        #region Classes

        private class DataItem
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public double Delta { get; set; }
        }

        #endregion Classes

        #region Methods

        public static void Main(string[] args)
        {
            var result = new ArgsParser(args)
                .Comment("small sample showing how to use \"infinite lists\" (streamed serialization/deserialization)")
                .Help("?", "help")
                .Keys("consumer").Tip("reads data from infinite list").Subparser(ConsumerMode)
                .Keys("fly").Tip("writes big amount of generated on the fly data to infinite list").Subparser(FlyMode)
                .Keys("db").Tip("writes big amount of data from DB to infinite list").Subparser(DbMode)
                .Keys("gen").Tip("generate test DB with data").Subparser(GenMode)
                .Result();

            if (result != null)
                Console.WriteLine(result);
        }

        private static void ConsumerMode(ArgsParser parser)
        {
            if (parser
                    .Keys("h", "host").Value(out var host, "http://localhost:8000/")
                    .Keys("s", "sleep").Value(out var sleep, 0)
                    .Keys("v", "verbose").Flag(out var verbose)
                    .Result() != null)
                return;

            var serializer = new JsonSerializer();
            var httpClient = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };

            using (var stream = httpClient.GetStreamAsync(host).Result)
            {
                using (var streamReader = new StreamReader(stream))
                {
                    var counter = 0;
                    using (var jsonReader = new JsonTextReader(streamReader))
                    {
                        jsonReader.Read();
                        if (jsonReader.TokenType != JsonToken.StartArray)
                            throw new Exception("Incorrect JSON");

                        jsonReader.Read();
                        while (jsonReader.TokenType == JsonToken.StartObject)
                        {
                            var obj = serializer.Deserialize<DataItem>(jsonReader);
                            counter += 1;

                            switch (verbose)
                            {
                                case SHOW_STAT when counter % SHOW_STAT_EVERY == 0:
                                    Console.WriteLine("Readed {0}..", counter);
                                    break;
                                case SHOW_ITEM:
                                    Console.WriteLine($"{obj.Id} - {obj.Name} - {obj.Delta}");
                                    break;
                            }

                            if (jsonReader.TokenType != JsonToken.EndObject)
                                throw new Exception("Incorrect JSON");
                            jsonReader.Read();

                            if (sleep > 0)
                                Thread.Sleep(sleep);
                        }

                        if (jsonReader.TokenType != JsonToken.EndArray)
                            throw new Exception("Incorrect JSON");
                    }

                    Console.WriteLine("Total items: {0}", counter);
                }
            }
        }

        private static void FlyMode(ArgsParser parser)
        {
            if (parser
                    .Keys("p", "port").Value(out var port, 8000)
                    .Keys("f", "flush").Value(out var flush, 1000)
                    .Keys("c", "count").Value(out var count, 10000)
                    .Keys("s", "sleep").Value(out var sleep, 0)
                    .Result() != null)
                return;

            var serializer = new JsonSerializer();

            var listener = new HttpServer(IPAddress.Any, port);
            listener.OnGet += (sender, args) =>
            {
                Console.WriteLine("Connected");

                args.Response.SendChunked = true;
                using (var streamWriter = new StreamWriter(args.Response.OutputStream))
                {
                    using (var jsonWriter = new JsonTextWriter(streamWriter))
                    {
                        var counter = 0;

                        jsonWriter.WriteStartArray();
                        while (counter < count)
                        {
                            serializer.Serialize(jsonWriter, GenItem(counter));

                            counter += 1;
                            if (counter % flush == 0)
                                jsonWriter.Flush();

                            if (sleep > 0)
                                Thread.Sleep(sleep);
                        }

                        jsonWriter.WriteEnd();
                    }
                }

                Console.WriteLine("Finished");
            };
            listener.Start();
            Console.WriteLine("Listening...");

            for (;;)
                Console.ReadLine();
        }

        private static void DbMode(ArgsParser parser)
        {
            if (parser
                    .Keys("p", "port").Value(out var port, 8000)
                    .Keys("d", "database").Value(out var connectionString, "Server=localhost;Trusted_Connection=True;")
                    .Keys("f", "flush").Value(out var flush, 1000)
                    .Keys("s", "sleep").Value(out var sleep, 0)
                    .Result() != null)
                return;

            var serializer = new JsonSerializer();

            var listener = new HttpServer(IPAddress.Any, port);
            listener.OnGet += (sender, args) =>
            {
                Console.WriteLine("Connected");

                args.Response.SendChunked = true;
                using (var streamWriter = new StreamWriter(args.Response.OutputStream))
                {
                    using (var connection = new SqlConnection(connectionString))
                    {
                        connection.Open();

                        new SqlCommand("USE InfinityLists;", connection).ExecuteNonQuery();

                        using (var reader = new SqlCommand("SELECT Id, Name, Delta FROM Data", connection).ExecuteReader())
                        {
                            using (var jsonWriter = new JsonTextWriter(streamWriter))
                            {
                                var counter = 0;

                                jsonWriter.WriteStartArray();
                                while (reader.Read())
                                {
                                    serializer.Serialize(jsonWriter, new DataItem
                                    {
                                        Id = reader.GetInt32(0),
                                        Name = reader.GetString(1),
                                        Delta = reader.GetDouble(2),
                                    });

                                    counter += 1;
                                    if (counter % flush == 0)
                                        jsonWriter.Flush();

                                    if (sleep > 0)
                                        Thread.Sleep(sleep);
                                }
                                jsonWriter.WriteEnd();
                            }
                        }
                    }
                }

                Console.WriteLine("Finished");
            };

            listener.Start();
            Console.WriteLine("Listening...");

            for (;;)
                Console.ReadLine();
        }

        private static void GenMode(ArgsParser parser)
        {
            if (parser
                    .Keys("d", "database").Value(out var connectionString, "Server=localhost;Trusted_Connection=True;")
                    .Keys("c", "count").Value(out var count, 10000)
                    .Result() != null)
                return;

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                if (new SqlCommand("SELECT DB_ID('InfinityLists')", connection).ExecuteScalar() == DBNull.Value)
                    new SqlCommand("CREATE DATABASE InfinityLists;", connection).ExecuteNonQuery();
                new SqlCommand("USE InfinityLists;", connection).ExecuteNonQuery();

                new SqlCommand("IF OBJECT_ID('Data') IS NOT NULL DROP TABLE Data;", connection).ExecuteNonQuery();
                new SqlCommand("CREATE TABLE Data (Id int, Name varchar(255), Delta float);", connection).ExecuteNonQuery();

                var appender = new SqlCommand("INSERT INTO Data (Id, Name, Delta) VALUES (@id, @name, @delta);", connection);
                appender.Parameters.Add("@id", SqlDbType.Int);
                appender.Parameters.Add("@name", SqlDbType.VarChar, 255);
                appender.Parameters.Add("@delta", SqlDbType.Float);
                appender.Prepare();

                for (var i = 0; i < count; ++i)
                {
                    var item = GenItem(i);
                    appender.Parameters["@id"].Value = item.Id;
                    appender.Parameters["@name"].Value = item.Name;
                    appender.Parameters["@delta"].Value = item.Delta;
                    appender.ExecuteNonQuery();

                    if (i % SHOW_STAT_EVERY == 0)
                        Console.WriteLine("Written {0}...", i);
                }

                Console.WriteLine("Total items: {0}", count);
            }
        }

        private static DataItem GenItem(int i)
        {
            return new DataItem
            {
                Id = i,
                Name = $"DataItem {i}",
                Delta = Math.Sin(i)
            };
        }

        #endregion Methods
    }
}
