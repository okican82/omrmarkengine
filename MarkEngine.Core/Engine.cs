﻿using AForge.Imaging.Filters;
using OmrMarkEngine.Core.Processor;
using OmrMarkEngine.Output;
using OmrMarkEngine.Template;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ZXing;

namespace OmrMarkEngine.Core
{
    /// <summary>
    /// OMR Engine Root
    /// </summary>
    public class Engine
    {
        /// <summary>
        /// When true indicates that the process should save intermediate images
        /// </summary>
        public bool SaveIntermediaryImages { get; set; }

        /// <summary>
        /// Apply template
        /// </summary>
        /// <param name="template"></param>
        /// <param name="image"></param>
        public OmrPageOutput ApplyTemplate(OmrTemplate template, ScannedImage image)
        {

            // Image ready for scan
            if (!image.IsReadyForScan)
            {
                if (!image.IsScannable)
                    image.Analyze();
                image.PrepareProcessing();
            }

            // Page output
            OmrPageOutput retVal = new OmrPageOutput()
            {
                Id = image.TemplateName + DateTime.Now.ToString("yyyyMMddHHmmss"),
                TemplateId = image.TemplateName,
                Parameters = image.Parameters, 
                StartTime = DateTime.Now
            };

            // Save directory for output images 
            string saveDirectory = String.Empty;
            var parmStr = new StringBuilder();
            if (this.SaveIntermediaryImages)
            {
                foreach (var pv in image.Parameters)
                    parmStr.AppendFormat("{0}.", pv);
                retVal.RefImages = new List<string>()
                {
                    String.Format("{0}-{1}-init.bmp", retVal.Id, parmStr),
                    String.Format("{0}-{1}-tx.bmp", retVal.Id, parmStr),
                    String.Format("{0}-{1}-fields.bmp", retVal.Id, parmStr),
                    String.Format("{0}-{1}-gs.bmp", retVal.Id, parmStr),
                    String.Format("{0}-{1}-bw.bmp", retVal.Id, parmStr),
                    String.Format("{0}-{1}-inv.bmp", retVal.Id, parmStr)
                };

                saveDirectory = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location), "imgproc");

                if (!Directory.Exists(saveDirectory))
                    Directory.CreateDirectory(saveDirectory);
                foreach (var pv in image.Parameters)
                    parmStr.AppendFormat("{0}.", pv);
                image.Image.Save(Path.Combine(saveDirectory, string.Format("{0}-{1}-init.bmp", DateTime.Now.ToString("yyyyMMddHHmmss"), parmStr)));
                
            }

            // First, we want to get the image from the scanned image and translate it to the original
            // position in the template
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap((int)template.BottomRight.X, (int)template.BottomRight.Y, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

                // Scale
                float width = template.TopRight.X - template.TopLeft.X,
                    height = template.BottomLeft.Y - template.TopLeft.Y;

                // Translate to original
                using (Graphics g = Graphics.FromImage(bmp))
                    g.DrawImage(image.Image, template.TopLeft.X, template.TopLeft.Y, width, height);

                if (this.SaveIntermediaryImages)
                    bmp.Save(Path.Combine(saveDirectory, string.Format("{0}-{1}-tx.bmp", DateTime.Now.ToString("yyyyMMddHHmmss"), parmStr)));


                // Now try to do hit from the template
                if (this.SaveIntermediaryImages)
                {
                    using (var tbmp = bmp.Clone() as Bitmap)
                    {
                        using (Graphics g = Graphics.FromImage(tbmp))
                        {
                            foreach (var field in template.Fields)
                            {
                                g.DrawRectangle(Pens.Black, field.TopLeft.X, field.TopLeft.Y, field.TopRight.X - field.TopLeft.X, field.BottomLeft.Y - field.TopLeft.Y);
                                g.DrawString(field.Id, SystemFonts.CaptionFont, Brushes.Black, field.TopLeft);
                            }
                        }

                        tbmp.Save(Path.Combine(saveDirectory, string.Format("{0}-{1}-fields.bmp", DateTime.Now.ToString("yyyyMMddHHmmss"), parmStr)));
                    }
                }


                // Now convert to Grayscale
                GrayscaleY grayFilter = new GrayscaleY();
                var gray = grayFilter.Apply(bmp);
                bmp.Dispose();
                bmp = gray;

                // Prepare answers
                Dictionary<OmrQuestionField, OmrOutputData> hitFields = new Dictionary<OmrQuestionField, OmrOutputData>();
                BarcodeReader barScan = new BarcodeReader();
                barScan.Options.TryHarder = false;
                barScan.TryInverted = true;
                foreach(var itm in template.Fields.Where(o=>o is OmrBarcodeField))
                {
                    PointF position = itm.TopLeft;
                    SizeF size = new SizeF(itm.TopRight.X - itm.TopLeft.X, itm.BottomLeft.Y - itm.TopLeft.Y);
                    using (var areaOfInterest = new Crop(new Rectangle((int)position.X, (int)position.Y, (int)size.Width, (int)size.Height)).Apply(bmp))
                        {
                            // Scan the barcode
                            var result = barScan.Decode(areaOfInterest);
                            if (result != null)
                                hitFields.Add(itm, new OmrBarcodeData()
                                {
                                    BarcodeData = result.Text,
                                    Format = result.BarcodeFormat,
                                    Id = itm.Id,
                                    TopLeft = new PointF(result.ResultPoints[0].X + position.X, result.ResultPoints[0].Y + position.Y),
                                    BottomRight = new PointF(result.ResultPoints[1].X + position.X, result.ResultPoints[0].Y + position.Y + 10)
                                });
                        }

                }

                if (this.SaveIntermediaryImages)
                    bmp.Save(Path.Combine(saveDirectory, string.Format("{0}-{1}-gs.bmp", DateTime.Now.ToString("yyyyMMddHHmmss"), parmStr)));

                // Now binarize
                Threshold binaryThreshold = new Threshold(120);
                binaryThreshold.ApplyInPlace(bmp);

                if (this.SaveIntermediaryImages)
                    bmp.Save(Path.Combine(saveDirectory, string.Format("{0}-{1}-bw.bmp", DateTime.Now.ToString("yyyyMMddHHmmss"), parmStr)));

                // Set return parameters
                String tAnalyzeFile = Path.Combine(Path.GetTempPath(), Path.GetTempFileName());
                bmp.Save(tAnalyzeFile, System.Drawing.Imaging.ImageFormat.Jpeg);
                retVal.AnalyzedImage = tAnalyzeFile;
                retVal.BottomRight = new PointF(bmp.Width, bmp.Height);

                // Now Invert 
                Invert invertFiter = new Invert();
                invertFiter.ApplyInPlace(bmp);

                if (this.SaveIntermediaryImages)
                    bmp.Save(Path.Combine(saveDirectory, string.Format("{0}-{1}-inv.bmp", DateTime.Now.ToString("yyyyMMddHHmmss"), parmStr)));

                
                // Iterate over remaining fields
                foreach(var itm in template.Fields.Where(o=>o is OmrBubbleField))
                {
                    PointF position = itm.TopLeft;
                    SizeF size = new SizeF(itm.TopRight.X - itm.TopLeft.X, itm.BottomLeft.Y - itm.TopLeft.Y);
                    using (var areaOfInterest = new Crop(new Rectangle((int)position.X, (int)position.Y, (int)size.Width, (int)size.Height)).Apply(bmp))
                    {
                        try
                        {
                            ExtractBiggestBlob blobFilter = new ExtractBiggestBlob();
                            using (var blob = blobFilter.Apply(areaOfInterest))
                                if (blob != null)
                                {
                                    var area = new AForge.Imaging.ImageStatistics(blob).PixelsCountWithoutBlack;
                                    if (area < 3) continue;
                                    var bubbleField = itm as OmrBubbleField;
                                    hitFields.Add(itm, new OmrBubbleData()
                                    {
                                        Id = itm.Id,
                                        Key = bubbleField.Question,
                                        Value = bubbleField.Value,
                                        TopLeft = new PointF(blobFilter.BlobPosition.X + position.X, blobFilter.BlobPosition.Y + position.Y),
                                        BottomRight = new PointF(blobFilter.BlobPosition.X + blob.Width + position.X, blobFilter.BlobPosition.Y + blob.Height + position.Y),
                                        BlobArea = area
                                    });
                                }
                        }
                        catch { }
                    }
                    
                }

                // Organize the response 
                foreach(var res in hitFields)
                {
                    if (String.IsNullOrEmpty(res.Key.AnswerRowGroup))
                    {
                        if(!retVal.AlreadyAnswered(res.Value))
                            retVal.Details.Add(res.Value);
                    }
                    else
                    {
                        // Rows of data
                        OmrRowData rowGroup = retVal.Details.Find(o => o.Id == res.Key.AnswerRowGroup) as OmrRowData;
                        if(rowGroup == null)
                        {
                            rowGroup = new OmrRowData()
                            {
                                Id = res.Key.AnswerRowGroup
                            };
                            retVal.Details.Add(rowGroup);
                        }

                        // Now add answer
                        if (!rowGroup.AlreadyAnswered(res.Value))
                            rowGroup.Details.Add(res.Value);
                    }
                }

                // Remove temporary images
                //foreach (var f in retVal.RefImages)
                //    File.Delete(Path.Combine(saveDirectory, f));

                // Outcome is success
                retVal.Outcome = OmrScanOutcome.Success;
            }
            catch(Exception e)
            {
                retVal.Outcome = OmrScanOutcome.Failure;
                retVal.ErrorMessage = e.Message;
                Trace.TraceError(e.ToString());
            }
            finally
            {
                retVal.StopTime = DateTime.Now;
                bmp.Dispose();
            }

            return retVal;
        }

             
    }
}