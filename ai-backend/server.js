const express = require("express");
const cors = require("cors");
const path = require("path");
require("dotenv").config();

const app = express();

/* =====================================================
   BASIC SERVER SETTINGS
===================================================== */

app.use(cors());

app.use(
    express.json({
        limit: "25mb"
    })
);


/* =====================================================
   SERVE AI ASSISTANT
===================================================== */

app.get("/", (req, res) => {

    res.sendFile(
        path.join(
            __dirname,
            "..",
            "ai-assistant.html"
        )
    );

});


/* =====================================================
   NORMAL CHAT
===================================================== */

app.post("/ask", async (req, res) => {

    try {

        const question =
            req.body.question;

        if (!question || !question.trim()) {

            return res.status(400).json({

                answer:
                    "Please enter a question."

            });

        }


        /*
           Abort request if Groq takes too long.
        */

        const controller =
            new AbortController();

        const timeout =
            setTimeout(
                () => controller.abort(),
                60000
            );


        const response =
            await fetch(
                "https://api.groq.com/openai/v1/chat/completions",
                {

                    method: "POST",

                    signal:
                        controller.signal,

                    headers: {

                        "Content-Type":
                            "application/json",

                        "Authorization":
                            `Bearer ${process.env.GROQ_API_KEY}`

                    },

                    body: JSON.stringify({

                        model:
                            "openai/gpt-oss-20b",

                        messages: [

                            {

                                role:
                                    "system",

                                content:
                                    `
You are a highly capable CSE and AIML study assistant.

Give accurate, clear and useful answers.

IMPORTANT MATHEMATICS FORMATTING:
- Write fractions normally, for example: 12/5
- Never use LaTeX such as \\frac{12}{5}
- Never use $$, \\[, \\], or other LaTeX delimiters
- Write powers like x^2
- Write square roots like sqrt(x)
- Avoid unusual Unicode mathematical symbols when plain text is clearer
- Never replace equations with boxes or malformed symbols

PROGRAMMING:
- You can solve complex programming problems.
- Support Python, JavaScript, Java, C, C++, C#, HTML, CSS, SQL and other common languages.
- When code is requested, provide complete and usable code when appropriate.
- Always preserve indentation.
- Put code inside Markdown code blocks.
- Explain important parts of the code.
- For debugging, identify the likely cause and provide the corrected code.
- For algorithms, explain the approach and complexity when useful.

CSE/AIML:
- Explain concepts from beginner to advanced level.
- For difficult problems, break the solution into logical steps.
- Do not unnecessarily shorten technically important explanations.

ANSWER STYLE:
- Use plain, clean text.
- Use headings and bullet points when useful.
- Do not output malformed mathematical notation.
- Be concise when the question is simple.
- Give more detail when the problem is complex.
`
                            },

                            {

                                role:
                                    "user",

                                content:
                                    question.trim()

                            }

                        ],

                        max_completion_tokens:
                            4096,

                        temperature:
                            0.2

                    })

                }
            );


        clearTimeout(timeout);


        const data =
            await response.json();


        if (!response.ok) {

            console.error(
                "Groq Chat Error:",
                data
            );


            const message =
                data?.error?.message ||
                "Unknown Groq API error.";


            return res.status(response.status).json({

                answer:
                    "AI server error:\n\n" +
                    message

            });

        }


        const answer =
            data?.choices?.[0]?.message?.content;


        if (!answer) {

            return res.status(500).json({

                answer:
                    "The AI returned an empty answer."

            });

        }


        res.json({

            answer:
                answer.trim()

        });


    } catch (error) {

        console.error(
            "Chat Server Error:",
            error
        );


        if (error.name === "AbortError") {

            return res.status(504).json({

                answer:
                    "The AI request took too long. Please try again."

            });

        }


        res.status(500).json({

            answer:
                "Could not connect to the AI server.\n\n" +
                "Make sure server.js is running."

        });

    }

});


/* =====================================================
   SCREEN DIAGNOSTIC ENDPOINT
===================================================== */

/*
   This endpoint intentionally does NOT send screenshots
   to an AI service.

   The SEB client can use its own local screenshot/
   preview functionality without uploading exam content.
*/

app.post(
    "/analyze-screen",
    async (req, res) => {

        try {

            const image =
                req.body.image;


            if (!image) {

                return res.status(400).json({

                    answer:
                        "No screenshot was received."

                });

            }


            /*
               The screenshot is deliberately not uploaded
               to Groq.

               Return a clear response so the client can
               distinguish the local capture from AI analysis.
            */

            res.json({

                answer:
                    "Screenshot captured successfully.\n\n" +
                    "Local screen capture is working, " +
                    "but AI screen analysis is disabled."

            });


        } catch (error) {

            console.error(
                "Screen Diagnostic Error:",
                error
            );


            res.status(500).json({

                answer:
                    "Screen diagnostic error."

            });

        }

    }
);


/* =====================================================
   HEALTH CHECK
===================================================== */

app.get("/health", (req, res) => {

    res.json({

        status:
            "ok",

        chat:
            "available",

        screenCapture:
            "local-only",

        model:
            "openai/gpt-oss-20b"

    });

});


/* =====================================================
   START SERVER
===================================================== */

app.listen(
    3000,
    () => {

        console.log(
            "AI Assistant running at http://localhost:3000"
        );

        console.log(
            "Chat model: openai/gpt-oss-20b"
        );

        console.log(
            "Groq API key loaded:",
            process.env.GROQ_API_KEY
                ? "yes"
                : "NO"
        );

    }
);