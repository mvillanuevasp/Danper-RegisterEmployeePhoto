using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.S3;
using Amazon.S3.Transfer;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using HttpMultipartParser;
using System.Net;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace RegisterEmployeePhoto
{


    public class Function
    {
        private readonly IAmazonS3 s3Client;
        private readonly AmazonDynamoDBClient dynamoClient;
        private const string tableName = "EMPLOYEES_PHOTOS";
        private const string bucketName = "photos-for-attendance-control";
        public Function()
        {
            s3Client = new AmazonS3Client();
            dynamoClient = new AmazonDynamoDBClient();
        }

        public async Task<object> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            try
            {
                var contentType = request.Headers.FirstOrDefault(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value;

                if (string.IsNullOrEmpty(contentType) || !contentType.Contains("boundary="))
                    throw new Exception("Invalid content-type header: boundary missing");

                var boundary = contentType.Split("boundary=")[1];
                if (boundary.StartsWith("\"") && boundary.EndsWith("\""))
                    boundary = boundary.Trim('"');

                var bodyBytes = Convert.FromBase64String(request.Body);

                var parser = MultipartFormDataParser.Parse(new MemoryStream(bodyBytes), boundary);

                if (string.IsNullOrEmpty(request.Body))
                    return ErrorResponse("El body está vacío.");
                
                if (string.IsNullOrEmpty(contentType))
                    return ErrorResponse("No se encontró Content-Type en las cabeceras.");

                if (!contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                    return ErrorResponse("El contenido debe ser multipart/form-data.");

                var dni = parser.GetParameterValue("dni");
                var nombres = parser.GetParameterValue("nombres");
                var file = parser.Files?.FirstOrDefault();

                if (string.IsNullOrEmpty(contentType))
                    return ErrorResponse("El dni es requerido");

                if (file == null)
                    return ErrorResponse("El archivo de foto es requerido");

                string extension = Path.GetExtension(file.FileName);
                if (string.IsNullOrEmpty(extension))
                {
                    extension = file.ContentType switch
                    {
                        "image/png" => ".png",
                        "image/jpeg" => ".jpg",
                        "image/jpg" => ".jpg",
                        _ => ".jpg"
                    };
                }

                string key = $"workforce/{dni}{extension}";

               
                var transferUtility = new TransferUtility(s3Client);
                file.Data.Position = 0;

                var uploadRequest = new TransferUtilityUploadRequest
                {
                    InputStream = file.Data,
                    Key = key,
                    BucketName = bucketName,
                    ContentType = file.ContentType,
                    //CannedACL = S3CannedACL.PublicRead,
                    Headers = { CacheControl = "no-cache" }
                };
                await transferUtility.UploadAsync(uploadRequest);

                string fileUrl = $"https://{bucketName}.s3.amazonaws.com/{key}";
                var table = Table.LoadTable(dynamoClient, tableName);
                var doc = new Document
                {
                    ["Dni"] = dni,
                    ["Active"] = new DynamoDBBool(true),
                    ["Name"] = nombres,
                    ["S3Key"] = key,
                    ["S3Url"] = fileUrl,
                    ["CreatedAt"] = DateTime.UtcNow.ToString("o")
                };
                await table.PutItemAsync(doc);

                return new
                {
                    statusCode = (int)HttpStatusCode.OK,
                    success = true,
                    message = "Foto subida correctamente"                    
                };
            }
            catch (Exception ex)
            {
                context.Logger.LogError(ex.ToString());
                return ErrorResponse("Error interno: " + ex.Message);
            }
        }

        private object ErrorResponse(string message)
        {
            return new { statusCode = (int)HttpStatusCode.BadRequest, success = false, message = message };
        }
    }

}
