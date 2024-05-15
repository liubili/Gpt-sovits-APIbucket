using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using System.Collections.Concurrent;
using CommunityToolkit.HighPerformance.Buffers;
using System.Collections;





class Program { 
class AudioQueue
{
    private ConcurrentDictionary<string, int> queue = new ConcurrentDictionary<string, int>();

    public void Enqueue(string fileName)
    {
        queue[fileName] = queue.Count;
    }

    public int GetPosition(string fileName)
    {
        return queue.TryGetValue(fileName, out int position) ? position : -1;
    }

    public void Dequeue(string fileName)
    {
        queue.TryRemove(fileName, out _);
        foreach (var key in queue.Keys)
        {
            queue[key]--;
        }
    }
}

static readonly HttpClient client = new HttpClient
{
    Timeout = TimeSpan.FromMinutes(10)
};

static async Task Main(string[] args)
{
    var minio = new MinioClient().WithEndpoint("127.0.0.1:9000").WithCredentials("minioadmin", "minioadmin").Build().WithSSL(false);
    AudioQueue audioQueue = new AudioQueue();

    HttpListener listener = new();
    listener.Prefixes.Add("http://127.0.0.1:5000/");
    listener.Start();
    Console.WriteLine("Listening...");

    while (true)
    {
        HttpListenerContext context = listener.GetContext();
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        string jsonRaw = new StreamReader(request.InputStream, request.ContentEncoding).ReadToEnd();
        if (string.IsNullOrEmpty(jsonRaw))
        {
            Console.WriteLine("错误: 接收到空的JSON数据");
            continue;
        }

        Dictionary<string, string> data;
        try
        {
            data = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonRaw);
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"错误: 无效的JSON数据 - {ex.Message}");
            continue;
        }

        if (request.HttpMethod == "POST" && request.RawUrl == "/")
        {
            try
            {
                string text = data.ContainsKey("text") ? data["text"] : throw new KeyNotFoundException("The key 'text' was not present in the dictionary.");
                string uniqueFileName = Guid.NewGuid().ToString() + ".wav";
                audioQueue.Enqueue(uniqueFileName);

                if (!data.ContainsKey("people"))
                {
                    throw new KeyNotFoundException("The key 'people' was not present in the dictionary.");
                }

                Task.Run(async () =>
                {
                    await Handle(text, data["people"], uniqueFileName, minio, audioQueue, client);
                     audioQueue.Dequeue(uniqueFileName);
                });

                // 创建一个字典来存储要返回的数据
                Dictionary<string, string> responseData = new Dictionary<string, string>();
                responseData["fileName"] = uniqueFileName;

                // 将字典转换为JSON字符串
                string jsonString = JsonConvert.SerializeObject(responseData);

                byte[] buffer = Encoding.UTF8.GetBytes(jsonString);

                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (KeyNotFoundException e)
            {
                // 处理KeyNotFoundException
                Console.WriteLine($"Error: {e.Message}");
            }
            catch (Exception e)
            {
                // 处理其他类型的异常
                Console.WriteLine($"An error occurred: {e.Message}");
            }
        }

        else if (request.HttpMethod == "POST" && request.RawUrl.StartsWith("/gt"))
        {
            string uniqueFileName = data["uniqueFileName"]; // 读取"uniqueFileName"键对应的值

            int positionInQueue = audioQueue.GetPosition(uniqueFileName);
            if (positionInQueue >= 0)
            {
                Dictionary<string, string> responseData = new Dictionary<string, string>();
                responseData["message"] = positionInQueue.ToString();

                // 将字典转换为JSON字符串
                string jsonString = JsonConvert.SerializeObject(responseData);

                byte[] buffer = Encoding.UTF8.GetBytes(jsonString);
                await Console.Out.WriteLineAsync(jsonString);
                response.OutputStream.Write(buffer, 0, buffer.Length);
                }
                else
                {
                    Dictionary<string, string> responseData = new Dictionary<string, string>();
                    responseData["filePath"] = uniqueFileName;
                    string jsonString = JsonConvert.SerializeObject(responseData);
                    byte[] buffer = Encoding.UTF8.GetBytes(jsonString);
                    response.OutputStream.Write(buffer, 0, buffer.Length);
                }
            }

        response.Close();
    }
}

    static async Task Handle(string text, string people, string uniqueFileName, IMinioClient minio, AudioQueue audioQueue, HttpClient client)
    {
        string bucketName = "scl";
        string url = "http://127.0.0.1:";

        switch (people)
        {
            case "1":
                url += "9880";
                break;
            case "2":
                url += "9881";
                break;
            case "3":
                url += "9882";
                break;
            case "4":
                url += "9884";
                break;
            // 你可以根据需要添加更多的case
            default:
                Console.WriteLine($"错误: 无效的people值 - {people}");
                return;
        }

        try
        {
            var response = await PostRequest(url, text, "zh", client);

            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync();
                using var ms = new MemoryStream(bytes);
                await Console.Out.WriteLineAsync(bucketName);
                await Console.Out.WriteLineAsync(uniqueFileName);
                await Console.Out.WriteLineAsync(ms.ToString());
                await Console.Out.WriteLineAsync(minio.ToString());

                // 直接在主线程中调用WriteToBucket
                await WriteToBucket(bucketName, uniqueFileName, ms, minio);

                string filePath = Path.Combine("F:\\scl", uniqueFileName);
                await File.WriteAllBytesAsync(filePath, bytes);
                Console.WriteLine($"Audio saved to {filePath}");

            }
            else
            {
                Console.WriteLine($"错误: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"异常: {ex.Message}");
        }
    }

    // 将WriteToBucket改为异步函数
    static async Task WriteToBucket(string bucketName, string uniqueFileName, MemoryStream ms, IMinioClient minio)
    {
        var beArgs = new BucketExistsArgs()
            .WithBucket(bucketName);
        bool found = await minio.BucketExistsAsync(beArgs).ConfigureAwait(false);
        if (!found)
        {
            var mbArgs = new MakeBucketArgs()
                .WithBucket(bucketName);
            await minio.MakeBucketAsync(mbArgs).ConfigureAwait(false);
        }

        var poArgs = new PutObjectArgs()
         .WithBucket(bucketName)
         .WithObject(uniqueFileName)
         .WithStreamData(ms)
         .WithObjectSize(ms.Length) // 设置对象大小
         .WithContentType("audio/wav");
        await minio.PutObjectAsync(poArgs).ConfigureAwait(false);
        AudioQueue audioQueue = new AudioQueue();
        audioQueue.Dequeue(uniqueFileName);
    }


    static async Task<HttpResponseMessage> PostRequest(string url, string text, string textLanguage, HttpClient client)
{
    var json = $"{{\"text\": \"{text}\", \"text_language\": \"{textLanguage}\"}}";
    var data = new StringContent(json, Encoding.UTF8, "application/json");

    return await client.PostAsync(url, data);
}

    // 写入桶
 

}
