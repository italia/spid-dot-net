﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Xml;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml.Serialization;
using System.IO;
using TPCWare.Spid.Sdk.Schema;
using log4net;
using TPCWare.Spid.Sdk.IdP;
using System.Xml.Linq;

namespace TPCWare.Spid.Sdk
{
    public static class Saml2Helper
    {
        static ILog log = log4net.LogManager.GetLogger(typeof(Saml2Helper));

        /// <summary>
        /// Build a signed SAML request.
        /// </summary>
        /// <param name="uuid"></param>
        /// <param name="destination"></param>
        /// <param name="consumerServiceURL"></param>
        /// <param name="securityLevel"></param>
        /// <param name="certFile"></param>
        /// <param name="certPassword"></param>
        /// <param name="storeLocation"></param>
        /// <param name="storeName"></param>
        /// <param name="findType"></param>
        /// <param name="findValue"></param>
        /// <param name="identityProvider"></param>
        /// <param name="enviroment"></param>
        /// <returns>Returns a Base64 Encoded String of the SAML request</returns>
        public static string BuildPostSamlRequest(string uuid, string destination, string consumerServiceURL, int securityLevel,
                                                  X509Certificate2 certificate, IdentityProvider identityProvider, int enviroment)
        {
            if (string.IsNullOrWhiteSpace(uuid))
            {
                log.Error("Error on BuildPostSamlRequest: The uuid parameter is null or empty.");
                throw new ArgumentNullException("The uuid parameter can't be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(destination))
            {
                log.Error("Error on BuildPostSamlRequest: The destination parameter is null or empty.");
                throw new ArgumentNullException("The destination parameter can't be null or empty.");
            }

            if (string.IsNullOrWhiteSpace(consumerServiceURL))
            {
                log.Error("Error on BuildPostSamlRequest: The consumerServiceURL parameter is null or empty.");
                throw new ArgumentNullException("The consumerServiceURL parameter can't be null or empty.");
            }

            if (certificate == null)
            {
                log.Error("Error on BuildPostSamlRequest: The certificate parameter is null.");
                throw new ArgumentNullException("The certificate parameter can't be null.");
            }

            if (identityProvider == null)
            {
                log.Error("Error on BuildPostSamlRequest: The identityProvider parameter is null.");
                throw new ArgumentNullException("The identityProvider parameter can't be null.");
            }

            if (enviroment < 0 )
            {
                log.Error("Error on BuildPostSamlRequest: The enviroment parameter is less than zero.");
                throw new ArgumentNullException("The enviroment parameter can't be less than zero.");
            }

            DateTime now = DateTime.UtcNow;

            AuthnRequestType MyRequest = new AuthnRequestType
            {
                ID = "_" + uuid,
                Version = "2.0",
                IssueInstant = identityProvider.Now(now),
                Destination = destination,
                AssertionConsumerServiceIndex = (ushort)enviroment,
                AssertionConsumerServiceIndexSpecified = true,
                AttributeConsumingServiceIndex = 1,
                AttributeConsumingServiceIndexSpecified = true,
                ForceAuthn = (securityLevel > 1),
                ForceAuthnSpecified = (securityLevel > 1),
                Issuer = new NameIDType
                {
                    Value = consumerServiceURL.Trim(),
                    Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:entity",
                    NameQualifier = consumerServiceURL
                },
                NameIDPolicy = new NameIDPolicyType
                {
                    Format = "urn:oasis:names:tc:SAML:2.0:nameid-format:transient",
                    AllowCreate = true,
                    AllowCreateSpecified = true
                },
                Conditions = new ConditionsType
                {
                    NotBefore = identityProvider.NotBefore(now),
                    NotBeforeSpecified = true,
                    NotOnOrAfter = identityProvider.After(now.AddMinutes(10)),
                    NotOnOrAfterSpecified = true
                },
                RequestedAuthnContext = new RequestedAuthnContextType
                {
                    Comparison = AuthnContextComparisonType.minimum,
                    ComparisonSpecified = true,
                    ItemsElementName = new ItemsChoiceType7[] { ItemsChoiceType7.AuthnContextClassRef },
                    Items = new string[] { "https://www.spid.gov.it/SpidL" + securityLevel.ToString() }
                }
            };

            XmlSerializerNamespaces ns = new XmlSerializerNamespaces();
            ns.Add("saml2p", "urn:oasis:names:tc:SAML:2.0:protocol");


            StringWriter stringWriter = new StringWriter();
            XmlWriterSettings settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                Encoding = Encoding.UTF8
            };

            XmlWriter responseWriter = XmlTextWriter.Create(stringWriter, settings);
            XmlSerializer responseSerializer = new XmlSerializer(MyRequest.GetType());
            responseSerializer.Serialize(responseWriter, MyRequest, ns);
            responseWriter.Close();

            string samlString = stringWriter.ToString();
            stringWriter.Close();

            XmlDocument doc = new XmlDocument();
            doc.LoadXml(samlString);

            XmlElement signature = SigningHelper.SignDoc(doc, certificate, "_" + uuid);

            doc.DocumentElement.InsertBefore(signature, doc.DocumentElement.ChildNodes[1]);

            return Convert.ToBase64String(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + doc.OuterXml));
        }

        /// <summary>
        /// Get the IdP Response and extract metadata to the returned DTO class
        /// </summary>
        /// <param name="base64Response"></param>
        /// <returns>IdpSaml2Response</returns>
        public static IdpSaml2Response GetIdpSaml2Response(string base64Response)
        {
            const string VALUE_NOT_AVAILABLE = "N/A";
            string idpAsciiResponse;

            if (String.IsNullOrEmpty(base64Response))
            {
                log.Error("Error on GetSpidUserInfo: The base64Response parameter is null or empty.");
                throw new ArgumentNullException("The base64Response parameter can't be null or empty.");
            }

            try
            {
                idpAsciiResponse = Encoding.UTF8.GetString(Convert.FromBase64String(base64Response));
            }
            catch (Exception ex)
            {
                log.Error("Error on GetSpidUserInfo: Unable to convert base64 response to ascii string.");
                throw new ArgumentException("Unable to converto base64 response to ascii string.", ex);
            }

            try
            {
                // Verify signature
                XmlDocument xml = new XmlDocument { PreserveWhitespace = true };
                xml.LoadXml(idpAsciiResponse);
                if (!SigningHelper.VerifySignature(xml))
                {
                    log.Error("Error on GetSpidUserInfo: Unable to verify the signature of the IdP response.");
                    throw new Exception("Unable to verify the signature of the IdP response.");
                }

                // Parse XML document
                XDocument xdoc = new XDocument();
                xdoc = XDocument.Parse(idpAsciiResponse);

                string destination = VALUE_NOT_AVAILABLE;
                string id = VALUE_NOT_AVAILABLE;
                string inResponseTo = VALUE_NOT_AVAILABLE;
                DateTimeOffset issueInstant = DateTimeOffset.MinValue;
                string version = VALUE_NOT_AVAILABLE;
                string statusCodeValue = VALUE_NOT_AVAILABLE;
                string statusCodeInnerValue = VALUE_NOT_AVAILABLE;
                string statusMessage = VALUE_NOT_AVAILABLE;
                string statusDetail = VALUE_NOT_AVAILABLE;
                string assertionId = VALUE_NOT_AVAILABLE;
                DateTimeOffset assertionIssueInstant = DateTimeOffset.MinValue;
                string assertionVersion = VALUE_NOT_AVAILABLE;
                string assertionIssuer = VALUE_NOT_AVAILABLE;
                string subjectNameId = VALUE_NOT_AVAILABLE;
                string subjectConfirmationMethod = VALUE_NOT_AVAILABLE;
                string subjectConfirmationDataInResponseTo = VALUE_NOT_AVAILABLE;
                DateTimeOffset subjectConfirmationDataNotOnOrAfter = DateTimeOffset.MinValue;
                string subjectConfirmationDataRecipient = VALUE_NOT_AVAILABLE;
                DateTimeOffset conditionsNotBefore = DateTimeOffset.MinValue;
                DateTimeOffset conditionsNotOnOrAfter = DateTimeOffset.MinValue;
                string audience = VALUE_NOT_AVAILABLE;
                DateTimeOffset authnStatementAuthnInstant = DateTimeOffset.MinValue;
                string authnStatementSessionIndex = VALUE_NOT_AVAILABLE;
                Dictionary<string, string> spidUserInfo = new Dictionary<string, string>();

                // Extract response metadata
                XElement responseElement = xdoc.Elements("{urn:oasis:names:tc:SAML:2.0:protocol}Response").Single();
                destination = responseElement.Attribute("Destination").Value;
                id = responseElement.Attribute("ID").Value;
                inResponseTo = responseElement.Attribute("InResponseTo").Value;
                issueInstant = DateTimeOffset.Parse(responseElement.Attribute("IssueInstant").Value);
                version = responseElement.Attribute("Version").Value;

                // Extract Issuer metadata
                string issuer = responseElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}Issuer").Single().Value.Trim();

                // Extract Status metadata
                XElement StatusElement = responseElement.Descendants("{urn:oasis:names:tc:SAML:2.0:protocol}Status").Single();
                IEnumerable<XElement> statusCodeElements = StatusElement.Descendants("{urn:oasis:names:tc:SAML:2.0:protocol}StatusCode");
                statusCodeValue = statusCodeElements.First().Attribute("Value").Value.Replace("urn:oasis:names:tc:SAML:2.0:status:", "");
                statusCodeInnerValue = statusCodeElements.Count() > 1 ? statusCodeElements.Last().Attribute("Value").Value.Replace("urn:oasis:names:tc:SAML:2.0:status:", "") : VALUE_NOT_AVAILABLE;
                statusMessage = StatusElement.Elements("{urn:oasis:names:tc:SAML:2.0:protocol}StatusMessage").SingleOrDefault()?.Value ?? VALUE_NOT_AVAILABLE;
                statusDetail = StatusElement.Elements("{urn:oasis:names:tc:SAML:2.0:protocol}StatusDetail").SingleOrDefault()?.Value ?? VALUE_NOT_AVAILABLE;

                if (statusCodeValue == "Success")
                {
                    // Extract Assertion
                    XElement assertionElement = responseElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}Assertion").Single();
                    assertionId = assertionElement.Attribute("ID").Value;
                    assertionIssueInstant = DateTimeOffset.Parse(assertionElement.Attribute("IssueInstant").Value);
                    assertionVersion = assertionElement.Attribute("Version").Value;
                    assertionIssuer = assertionElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}Issuer").Single().Value.Trim();

                    // Extract Subject metadata
                    XElement subjectElement = assertionElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}Subject").Single();
                    subjectNameId = subjectElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}NameID").Single().Value.Trim();
                    subjectConfirmationMethod = subjectElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}SubjectConfirmation").Single().Attribute("Method").Value;
                    XElement confirmationDataElement = subjectElement.Descendants("{urn:oasis:names:tc:SAML:2.0:assertion}SubjectConfirmationData").Single();
                    subjectConfirmationDataInResponseTo = confirmationDataElement.Attribute("InResponseTo").Value;
                    subjectConfirmationDataNotOnOrAfter = DateTimeOffset.Parse(confirmationDataElement.Attribute("NotOnOrAfter").Value);
                    subjectConfirmationDataRecipient = confirmationDataElement.Attribute("Recipient").Value;

                    // Extract Conditions metadata
                    XElement conditionsElement = assertionElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}Conditions").Single();
                    conditionsNotBefore = DateTimeOffset.Parse(conditionsElement.Attribute("NotBefore").Value);
                    conditionsNotOnOrAfter = DateTimeOffset.Parse(conditionsElement.Attribute("NotOnOrAfter").Value);
                    audience = conditionsElement.Descendants("{urn:oasis:names:tc:SAML:2.0:assertion}Audience").Single().Value.Trim();

                    // Extract AuthnStatement metadata
                    XElement authnStatementElement = assertionElement.Elements("{urn:oasis:names:tc:SAML:2.0:assertion}AuthnStatement").Single();
                    authnStatementAuthnInstant = DateTimeOffset.Parse(authnStatementElement.Attribute("AuthnInstant").Value);
                    authnStatementSessionIndex = authnStatementElement.Attribute("SessionIndex").Value;

                    // Extract SPID user info
                    foreach (XElement attribute in xdoc.Descendants("{urn:oasis:names:tc:SAML:2.0:assertion}AttributeStatement").Elements())
                    {
                        spidUserInfo.Add(
                            attribute.Attribute("Name").Value,
                            attribute.Elements().Single(a => a.Name == "{urn:oasis:names:tc:SAML:2.0:assertion}AttributeValue").Value.Trim()
                        );
                    }
                }

                return new IdpSaml2Response(destination, id, inResponseTo, issueInstant, version, issuer,
                                            statusCodeValue, statusCodeInnerValue, statusMessage, statusDetail,
                                            assertionId, assertionIssueInstant, assertionVersion, assertionIssuer,
                                            subjectNameId, subjectConfirmationMethod, subjectConfirmationDataInResponseTo,
                                            subjectConfirmationDataNotOnOrAfter, subjectConfirmationDataRecipient,
                                            conditionsNotBefore, conditionsNotOnOrAfter, audience,
                                            authnStatementAuthnInstant, authnStatementSessionIndex,
                                            spidUserInfo);
            }
            catch (Exception ex)
            {
                log.Error("Error on GetSpidUserInfo: Unable to read metadata from SAML2 document (see raw response).");
                log.Error("RAW RESPONSE: " + idpAsciiResponse);
                throw new ArgumentException("Unable to read AttributeStatement attributes from SAML2 document.", ex);
            }
        }

    }
}
