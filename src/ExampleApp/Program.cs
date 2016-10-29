﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KafkaClient;
using KafkaClient.Common;
using KafkaClient.Protocol;

namespace ExampleApp
{
    class Program
    {
        static void Main(string[] args)
        {
            const string topicName = "TestHarness";

            //create an options file that sets up driver preferences
            var options = new KafkaOptions(new [] { new Uri("http://CSDKAFKA01:9092"), new Uri("http://CSDKAFKA02:9092") });

            //start an out of process thread that runs a consumer that will write all received messages to the console
            Task.Run(() =>
            {
                var consumer = new OldConsumer(new ConsumerOptions(topicName, new BrokerRouter(options)) { Log = new TraceLog() });
                foreach (var data in consumer.Consume())
                {
                    Console.WriteLine("Response: P{0},O{1} : {2}", data.PartitionId, data.Offset, data.Value.ToUtf8String());
                }
            });

            //create a producer to send messages with
            var producer = new Producer(new BrokerRouter(options), new ProducerConfiguration(batchSize: 100, batchMaxDelay: TimeSpan.FromSeconds(2)));

            //take in console read messages
            Console.WriteLine("Type a message and press enter...");
            while (true)
            {
                var message = Console.ReadLine();
                if (message == "quit") break;

                if (string.IsNullOrEmpty(message))
                {
                    //send a random batch of messages
                    SendRandomBatch(producer, topicName, 200);
                }
                else
                {
                    producer.SendMessageAsync(new Message(message), topicName, CancellationToken.None);
                }
            }

            using (producer)
            {

            }
        }

        private static async void SendRandomBatch(Producer producer, string topicName, int count)
        {
            //send multiple messages
            var sendTask = producer.SendMessagesAsync(Enumerable.Range(0, count).Select(x => new Message(x.ToString())), topicName, CancellationToken.None);
            
            Console.WriteLine("Posted #{0} messages. InFlight:{1} ActiveSenders:{2}", count, producer.InFlightMessageCount, producer.ActiveSenders);

            var response = await sendTask;

            Console.WriteLine("Completed send of batch: {0}. InFlight:{1} ActiveSenders:{2}", count, producer.InFlightMessageCount, producer.ActiveSenders);
            foreach (var result in response.OrderBy(x => x.PartitionId))
            {
                Console.WriteLine("Topic:{0} PartitionId:{1} Offset:{2}", result.TopicName, result.PartitionId, result.Offset);
            }
            
        }
    }
}
