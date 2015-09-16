﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Channels;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Digst.OioIdws.Logging;
using Digst.OioIdws.Utils;

namespace Digst.OioIdws.Transformers
{
    /// <summary>
    /// This class implements the signature case part of specification [NEMLOGIN-STSRULES].
    /// It expects a standard WS-Trust message and transforms it to a format that NemLog-in STS understands and vice versa.
    /// The specification does not seem to be SOAP 1.1 compliant as it does not accept the mustUnderstand attribute on wsa:Action and wsa:To SOAP headers.
    /// Furthermore, SOAP Fauls are not SOAP 1.1 compliant. Therefore, we convert them to be.
    /// Request message to STS regarding signature case must be on the following minimum format:
    /// <S11:Envelope>
    ///     <S11:Header>
    ///         <wsa:Action>...</wsa:Action>
    ///         <wsa:MessageID>...</wsa:MessageID>
    ///         <wsa:To>...</wsa:To>
    ///         <wsse:Security>...</wsse:Security>
    ///     </S11:Header>
    ///     <S11:Body>
    ///         <wst:RequestSecurityToken>
    ///             <wst:RequestType>...</wst:RequestType>
    ///             <wsp:AppliesTo>...</wsp:AppliesTo>
    ///         </wst:RequestSecurityToken>
    ///     </S11:Body>
    /// </S11:Envelope>
    /// </summary>
    public class SignatureCaseMessageTransformer : ISignatureCaseMessageTransformer
    {
        // Namespaces
        public const string WsaNamespace = "http://www.w3.org/2005/08/addressing";
        public const string S11Namespace = "http://schemas.xmlsoap.org/soap/envelope/";
        public const string Wsp12Namespace = "http://schemas.xmlsoap.org/ws/2004/09/policy"; // Corresponds to WS-Policy 1.2.
        public const string WspNamespace = "http://schemas.xmlsoap.org/ws/2002/12/policy"; // Corresponds to WS-SecurityPolicy
        public const string Wst13Namespace = "http://docs.oasis-open.org/ws-sx/ws-trust/200512";
        public const string WsuNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        public const string WsseNamespace = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        public const string VsDebuggerNamespace = "http://schemas.microsoft.com/vstudio/diagnostics/servicemodelsink";
        
        // STS Entity ID's
        public const string BootstrapTokenCaseEntityId = "https://bootstrap.sts.nemlog-in.dk"; // Currently not used
        public const string LocalTokenCaseEntityId = " https://local.sts.nemlog-in.dk"; // Currently not used
        public const string SignatureCaseEntityId = "https://signature.sts.nemlog-in.dk";
        
        // Token type
        public const string Saml20TokenType = "http://docs.oasis-open.org/wss/oasis-wss-saml-tokenprofile-1.1#SAMLV2.0";
        
        // ID values.
        private const string ActionIdValue = "action";
        private const string MessageIdIdValue = "msgid";
        private const string ToIdValue = "to";
        public const string TimeStampIdValue = "sec-ts";
        public const string BinarySecurityTokenIdValue = "sec-binsectoken";
        private const string BodyIdValue = "body";
        
        // Namespace prefixes
        public const string S11Prefix = "S11";
        public const string WsaPrefix = "wsa";
        public const string WsuPrefix = "wsu";
        public const string WssePrefix = "wsse";
        public const string WspPrefix = "wsp";
        public const string Wst13Prefix = "wst";

        // Datetime formats
        private const string WrongDateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        private const string CorrectDateTimeFormat = "yyyy-MM-ddTHH:mm:ssZ"; // Results in format 2015-01-14T14:50:24Z mandated by spec.


        public void ModifyMessageAccordingToStsNeeds(ref Message request, X509Certificate2 clientCertificate)
        {
            // Convert Message into a XML document that can be manipulated
            var xDocument = ConvertMessageToXml(request);

            // Log RST before being manipulated ... if we do not take the above two headers into account :)
            Logger.Instance.Debug("RST send to STS before being manipulated:\n" + xDocument);

            // Manipulate XML
            AddNamespacesToEnvelope(xDocument);
            ManipulateHeader(xDocument, clientCertificate);
            ManipulateBody(xDocument);
            SignMessage(ref xDocument, clientCertificate);

            // Log RST after being manipulated
            Logger.Instance.Debug("RST send to STS after being manipulated:\n" + xDocument);

            // Convert XML back to a Message
            request = ConvertXmlToMessage(request, xDocument);
        }

        public void ModifyMessageAccordingToWsTrust(ref Message response, X509Certificate2 stsCertificate)
        {
            // Convert Message into a XML document that can be manipulated
            var xDocument = ConvertMessageToXml(response);

            // Log RSTR before being manipulated
            Logger.Instance.Debug("RSTR recieved from STS before being manipulated:\n" + xDocument);

            // SOAP 1.1 faults from NemLog-in STS contains two Envelope elements. This hack removes the first element whereafter the .Net framework will see it as a fault.
            RemoveOuterEnvelopeElementIfMessageIsASoapFault(ref xDocument, ref response);

            // Fault response is not SOAP 1.1 compliant. We therefore need to change it so that other channels (e.g. WSTrust channel) are able to read it properly.
            if (response.IsFault)
            {
                // Remove default namespace in order for fault message to be soap 1.1 compliant.
                var namespaceManager = new XmlNamespaceManager(new NameTable());
                namespaceManager.AddNamespace("s", S11Namespace);
                namespaceManager.AddNamespace("wst", Wst13Namespace);
                var envelopeElement = xDocument.XPathSelectElement("/s:Envelope", namespaceManager);
                var xmlnsAttribute = envelopeElement.Attribute(XName.Get("xmlns"));
                xmlnsAttribute.Remove();

                // faultcode must contain a qualified name. However, NemLog-in has forgotten to specify the wst namespace in order to make the SOAP fault SOAP 1.1 compliant.
                var xmlnsWstAttribute = envelopeElement.Attribute(XNamespace.Xmlns + Wst13Prefix);
                if (xmlnsWstAttribute == null)
                    envelopeElement.Add(new XAttribute(XNamespace.Xmlns + Wst13Prefix, Wst13Namespace));

                // faultcode and faultstring must be in the empty namespace.
                var faultElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/s:Fault", namespaceManager);
                var faultcodeElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/s:Fault/wst:faultcode",
                    namespaceManager);
                faultcodeElement.Remove();
                var faultstringElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/s:Fault/wst:faultstring",
                    namespaceManager);
                faultstringElement.Remove();
                var newFaultCodeElement = new XElement("faultcode") {Value = faultcodeElement.Value};
                var newFaultStringElement = new XElement("faultstring") {Value = faultstringElement.Value};
                faultElement.Add(newFaultCodeElement);
                faultElement.Add(newFaultStringElement);
            }
            // Normal RSTR
            else
            {
                var namespaceManager = new XmlNamespaceManager(new NameTable());
                namespaceManager.AddNamespace("s", S11Namespace);
                namespaceManager.AddNamespace("wsse", WsseNamespace);
                namespaceManager.AddNamespace("wsu", WsuNamespace);
                namespaceManager.AddNamespace("wst", Wst13Namespace);

                // Verify signature before making any modifications
                if(!XmlSignatureUtils.VerifySignature(xDocument, stsCertificate))
                    throw new InvalidOperationException("SOAP signature recieved from STS does not validate!");

                // Expiry time are currently not on the format specified by the spec. The spec says yyyy-MM-ddTHH:mm:ssZ but yyyy-MM-ddTHH:mm:ss.fffZ is currently retrieved.
                // TODO: Code must be updated when response are following the spec.
                // Verify life time of SOAP message
                var messageExpireTimeElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/wsse:Security/wsu:Timestamp/wsu:Expires", namespaceManager);
                var messageExpireZuluTime = DateTime.ParseExact(messageExpireTimeElement.Value, WrongDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                var currentZuluTime = DateTime.UtcNow;
                if(currentZuluTime >= messageExpireZuluTime)
                    throw new InvalidOperationException("SOAP message has expired. Current Zulu time was: " + currentZuluTime + ", message Zulu expiry time was: " + messageExpireZuluTime);

                // Verify life time of RSTS
                var rstsExpireTimeElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/wst:RequestSecurityTokenResponseCollection/wst:RequestSecurityTokenResponse/wst:Lifetime/wsu:Expires", namespaceManager);
                var rstsExpireZuluTime = DateTime.ParseExact(rstsExpireTimeElement.Value, CorrectDateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                if (currentZuluTime >= rstsExpireZuluTime)
                    throw new InvalidOperationException("RSTS has expired. Current Zulu time was: " + currentZuluTime + ", RSTS Zulu expiry time was: " + rstsExpireZuluTime);

                ManipulateRstrBody(xDocument);
            }

            // Log RSTR before being manipulated
            Logger.Instance.Debug("RSTR recieved from STS after being manipulated:\n" + xDocument);

            // Convert XML back to a Message
            response = ConvertXmlToMessage(response, xDocument);
        }

        private static void RemoveOuterEnvelopeElementIfMessageIsASoapFault(ref XDocument xDocument, ref Message response)
        {
            var namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("s", S11Namespace);
            var faultElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/s:Envelope/s:Body/s:Fault",
                namespaceManager);
            if (faultElement != null)
            {
                var innerEnvelopeElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/s:Envelope", namespaceManager);
                xDocument = XDocument.Parse(innerEnvelopeElement.ToString());
                response = ConvertXmlToMessage(response, xDocument);
            }
        }

        private static void AddNamespacesToEnvelope(XDocument xDocument)
        {
            var namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("s", S11Namespace);
            var envelopeElement = xDocument.XPathSelectElement("/s:Envelope", namespaceManager);

            // First remove all namespaces and then readd them in order to control prefixes. 
            // This is really not necessary ... it was done to make it easier to compare with official examples that uses the prefixes specified below.
            envelopeElement.DescendantsAndSelf().Attributes().Where(n => n.IsNamespaceDeclaration).Remove();
            envelopeElement.Add(new XAttribute(XNamespace.Xmlns + S11Prefix, S11Namespace));
            envelopeElement.Add(new XAttribute(XNamespace.Xmlns + WsaPrefix, WsaNamespace));
            envelopeElement.Add(new XAttribute(XNamespace.Xmlns + WspPrefix, WspNamespace));
            envelopeElement.Add(new XAttribute(XNamespace.Xmlns + WssePrefix, WsseNamespace));
            envelopeElement.Add(new XAttribute(XNamespace.Xmlns + Wst13Prefix, Wst13Namespace));

            // This namespace is required. If not added then Id attributes is not correctly prefixed with "wsu" and RST becomes invalid according to standard.
            envelopeElement.Add(new XAttribute(XNamespace.Xmlns + WsuPrefix, WsuNamespace));
        }

        private static Message ConvertXmlToMessage(Message request, XNode xNode)
        {
            byte[] messageAsBytes;
            using (var memoryStream = new MemoryStream())
            {
                using (XmlWriter xw = XmlWriter.Create(memoryStream))
                {
                    xNode.WriteTo(xw);
                }
                // No need to set position to 0 as ToArray does not use this property.
                messageAsBytes = memoryStream.ToArray();
            }
            var xmlDictionaryReader = XmlDictionaryReader.CreateTextReader(messageAsBytes,
                XmlDictionaryReaderQuotas.Max);
            var newMessage = Message.CreateMessage(xmlDictionaryReader, int.MaxValue, request.Version);
            newMessage.Properties.CopyProperties(request.Properties);
            return newMessage;
        }

        private static void ManipulateHeader(XDocument xDocument, X509Certificate2 clientCertificate)
        {
            var namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("a", WsaNamespace);
            namespaceManager.AddNamespace("s", S11Namespace);
            namespaceManager.AddNamespace("vs", VsDebuggerNamespace);

            // Remove VS debugger element. It is only present when running in debug mode. So removing the element is just to make life easier for developers.
            // The spec states that all header elements (also those not used by the STS) must be included in the signature. Hence, we need to remove the debugger element.
            var vsDebuggerElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/vs:VsDebuggerCausalityData", namespaceManager);
            if (vsDebuggerElement != null)
            {
                vsDebuggerElement.Remove();
            }

            // Remove mustUnderstand attributes as it is not allowed by the specification.
            var actionElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/a:Action", namespaceManager);
            RemoveMustUnderstandAttribute(actionElement);

            // Because ManuelAddressing is set to true we need to manual add the messageID header
            var messageIdElement = new XElement(XName.Get("MessageID", WsaNamespace));
            messageIdElement.Value = "uuid:" + Guid.NewGuid().ToString("D");
            actionElement.AddAfterSelf(messageIdElement);

            // a:To is normally set to the URI of the service endpoint by the framework which is not what we need here and therefore we neeed to set it manually.
            // In order to work ... ManualAddressing must be set to true on HttpsTransportBindingElement or else the a:To is overwritten in the HttpsTransportChannel.
            var toElement = new XElement(XName.Get("To", WsaNamespace));
            toElement.Value = SignatureCaseEntityId;
            messageIdElement.AddAfterSelf(toElement);
            
            // Add Security element
            var createdElement = new XElement(XName.Get("Created", WsuNamespace));
            var currentTime = DateTime.UtcNow;
            createdElement.Value = currentTime.ToString(CorrectDateTimeFormat, CultureInfo.InvariantCulture);
            var expiresElement = new XElement(XName.Get("Expires", WsuNamespace));
            expiresElement.Value = currentTime.AddMinutes(5).ToString("s") + "Z"; // Make request expire after 5 minutes.
            var timestampElement = new XElement(XName.Get("Timestamp", WsuNamespace));
            timestampElement.Add(createdElement);
            timestampElement.Add(expiresElement);
            var binarySecurityTokenElement = new XElement(XName.Get("BinarySecurityToken", WsseNamespace));
            binarySecurityTokenElement.Add(new XAttribute("ValueType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-x509-token-profile-1.0#X509v3"));
            binarySecurityTokenElement.Add(new XAttribute("EncodingType", "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary"));
            binarySecurityTokenElement.Value = Convert.ToBase64String(clientCertificate.Export(X509ContentType.Cert));
            var securityElement = new XElement(XName.Get("Security", WsseNamespace));
            securityElement.Add(new XAttribute("mustUnderstand", "1"));
            securityElement.Add(timestampElement);
            securityElement.Add(binarySecurityTokenElement);
            var headerElement = xDocument.XPathSelectElement("/s:Envelope/s:Header", namespaceManager);
            headerElement.Add(securityElement);
        }

        private static void ManipulateBody(XDocument xDocument)
        {
            var namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("wsp12", Wsp12Namespace);
            namespaceManager.AddNamespace("s", S11Namespace);
            namespaceManager.AddNamespace("wsa", WsaNamespace);
            namespaceManager.AddNamespace("trust", Wst13Namespace);
            namespaceManager.AddNamespace("wsp", WspNamespace);
            namespaceManager.AddNamespace("wsu", WsuNamespace);

            // Add Context attribute to wst:RequestSecurityToken with a unique ID as value
            var requestSecurityTokenElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityToken", namespaceManager);
            requestSecurityTokenElement.Add(new XAttribute("Context", new UniqueId()));

            // Replace namespace http://schemas.xmlsoap.org/ws/2004/09/policy with http://schemas.xmlsoap.org/ws/2002/12/policy
            var appliesToElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityToken/wsp12:AppliesTo", namespaceManager);
            var endpointReferenceElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityToken/wsp12:AppliesTo/wsa:EndpointReference", namespaceManager);
            appliesToElement.Remove();
            var newAppliesToElement = new XElement(XName.Get("AppliesTo", WspNamespace));
            newAppliesToElement.Add(endpointReferenceElement);
            requestSecurityTokenElement.Add(newAppliesToElement);

            // Remove last '/' in endpoint address. Due to new URI(...) automatically adds an ending '/'.
            var addressReferenceElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityToken/wsp:AppliesTo/wsa:EndpointReference/wsa:Address", namespaceManager);
            RemoveEndingForwardSlash(addressReferenceElement);

            // Change lifetime expires format if present from "yyyy-MM-ddTHH:mm:ss.fffZ" to "yyyy-MM-ddTHH:mm:ssZ"
            var lifetimeElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityToken/trust:Lifetime/wsu:Expires", namespaceManager);
            if (lifetimeElement != null)
                lifetimeElement.Value =
                    DateTime.ParseExact(lifetimeElement.Value, WrongDateTimeFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal).ToString(CorrectDateTimeFormat, CultureInfo.InvariantCulture);
        }

        private static void ManipulateRstrBody(XDocument xDocument)
        {
            var namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("wsp12", Wsp12Namespace);
            namespaceManager.AddNamespace("s", S11Namespace);
            namespaceManager.AddNamespace("wsa", WsaNamespace);
            namespaceManager.AddNamespace("trust", Wst13Namespace);
            namespaceManager.AddNamespace("wsp", WspNamespace);

            // Replace namespace http://schemas.xmlsoap.org/ws/2002/12/policy with http://schemas.xmlsoap.org/ws/2004/09/policy 
            var requestSecurityTokenElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityTokenResponseCollection/trust:RequestSecurityTokenResponse", namespaceManager);
            var appliesToElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityTokenResponseCollection/trust:RequestSecurityTokenResponse/wsp:AppliesTo", namespaceManager);
            var endpointReferenceElement = xDocument.XPathSelectElement("/s:Envelope/s:Body/trust:RequestSecurityTokenResponseCollection/trust:RequestSecurityTokenResponse/wsp:AppliesTo/wsa:EndpointReference", namespaceManager);
            appliesToElement.Remove();
            var newAppliesToElement = new XElement(XName.Get("AppliesTo", Wsp12Namespace));
            newAppliesToElement.Add(endpointReferenceElement);
            requestSecurityTokenElement.Add(newAppliesToElement);
        }

        private static void RemoveEndingForwardSlash(XElement element)
        {
            var addressValue = element.Value;
            if (addressValue.EndsWith("/"))
            {
                element.Value = addressValue.Substring(0, addressValue.Length - 1);
            }
        }

        private static void SignMessage(ref XDocument xDocument, X509Certificate2 clientCertificate)
        {
            // Add id's to elements that needs to be signed.
            var namespaceManager = new XmlNamespaceManager(new NameTable());
            namespaceManager.AddNamespace("a", WsaNamespace);
            namespaceManager.AddNamespace("s", S11Namespace);
            namespaceManager.AddNamespace("wsse", WsseNamespace);
            namespaceManager.AddNamespace("wsu", WsuNamespace);
            var actionElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/a:Action", namespaceManager);
            var msgElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/a:MessageID", namespaceManager);
            var toElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/a:To", namespaceManager);
            var timeStampElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/wsse:Security/wsu:Timestamp", namespaceManager);
            var binarySecurityTokenElement = xDocument.XPathSelectElement("/s:Envelope/s:Header/wsse:Security/wsse:BinarySecurityToken", namespaceManager);
            var bodyElement = xDocument.XPathSelectElement("/s:Envelope/s:Body", namespaceManager);
            var idXName = XName.Get("Id", WsuNamespace);
            actionElement.Add(new XAttribute(idXName, ActionIdValue));
            msgElement.Add(new XAttribute(idXName, MessageIdIdValue));
            toElement.Add(new XAttribute(idXName, ToIdValue));
            timeStampElement.Add(new XAttribute(idXName, TimeStampIdValue));
            binarySecurityTokenElement.Add(new XAttribute(idXName, BinarySecurityTokenIdValue));
            bodyElement.Add(new XAttribute(idXName, BodyIdValue));

            var idOfElementsThatMustBeSigned = new List<string> {ActionIdValue, MessageIdIdValue, ToIdValue, TimeStampIdValue, BinarySecurityTokenIdValue, BodyIdValue};

            xDocument = XmlSignatureUtils.SignDocument(xDocument, idOfElementsThatMustBeSigned, clientCertificate);
        }

        private static XDocument ConvertMessageToXml(Message request)
        {
            XDocument xDocument;
            using (var memoryStream = new MemoryStream())
            {
                using (var xmlDictionaryWriter = XmlDictionaryWriter.CreateTextWriter(memoryStream))
                {
                    request.WriteMessage(xmlDictionaryWriter);
                    xmlDictionaryWriter.Flush();
                    memoryStream.Position = 0; // Needed in order for XDocument.Load to read stream from beginning.
                    xDocument = XDocument.Load(memoryStream);
                }
            }
            return xDocument;
        }

        private static void RemoveMustUnderstandAttribute(XElement element)
        {
            XAttribute mustUnderstandAttribute =
                element.Attribute(XName.Get("mustUnderstand", S11Namespace));
            if (mustUnderstandAttribute != null)
            {
                mustUnderstandAttribute.Remove();
            }
        }
    }
}