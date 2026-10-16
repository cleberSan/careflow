# Avisos de terceiros

O CareFlow é distribuído sob a licença MIT ([LICENSE](LICENSE)). Esta lista cobre os componentes de terceiros cuja licença pede que o aviso acompanhe o código ou o binário. Pacotes da Microsoft e do ecossistema .NET (ASP.NET Core, EF Core, Npgsql, OpenTelemetry e outros) seguem as próprias licenças MIT ou Apache-2.0, indicadas em cada pacote NuGet.

## Firely .NET SDK (HL7 FHIR)

Usado pelo serviço de pedidos do titular para montar e serializar o Bundle FHIR R4 do pacote (ADR-0012).

| Pacote | Versão | Onde | Licença |
|---|---|---|---|
| Hl7.Fhir.R4 | 6.7.0 | `CareFlow.Rights.Api` | BSD-3-Clause |
| Hl7.Fhir.Base | 6.7.0 | `CareFlow.Rights.Api` (dependência) | BSD-3-Clause |
| Hl7.Fhir.Conformance | 6.7.0 | `CareFlow.Rights.Api` (dependência) | BSD-3-Clause |
| Fhir.Metrics | 1.4.0 | `CareFlow.Rights.Api` (dependência) | BSD-3-Clause |
| Firely.Fhir.Validation.R4 | 3.5.0 | só testes | BSD-3-Clause |
| Hl7.Fhir.Specification.Data.R4 | 6.7.0 | só testes (perfis base do R4) | BSD-3-Clause |

Os perfis base dentro do pacote de especificação são material da especificação HL7 FHIR, publicada pela HL7 International sob CC0 (domínio público).

```text
BSD 3-Clause License

Copyright (c) 2013-2024, HL7, Firely (info@fire.ly), Microsoft Open Technologies 
and contributors. See the file CONTRIBUTORS for details

All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this
   list of conditions and the following disclaimer in the documentation and/or
   other materials provided with the distribution.

3. Neither the name of Firely nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Newtonsoft.Json

Dependência do Firely SDK, no serviço de pedidos. O CareFlow serializa com System.Text.Json.

| Pacote | Versão | Licença |
|---|---|---|
| Newtonsoft.Json | 13.0.4 | MIT |

```text
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## MessagePack.Annotations

Dependência do validador do Firely, só nos testes.

| Pacote | Versão | Licença |
|---|---|---|
| MessagePack.Annotations | 3.1.10 | MIT |

```text
MIT License

Copyright (c) 2017 Yoshifumi Kawai and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
