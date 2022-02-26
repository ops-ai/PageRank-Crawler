# PageRank-Crawler

Web scraper using Chrome via Puppeteer to crawl a website and create a multi-graph containing nodes for each page and keyword of relevance, and links between pages and page-keywords.
The goal of the project is to create a tool to evaluate the structure of a website and evaluate how appropriate the structure is to the intent of the site.

## Neo4j
It currently uses Neo4j 3.5 due to incompatibilities with the Neo4jClient library

### Example / useful queries

Export all data into a csv

    CALL apoc.export.csv.all("first.csv", {})


Get stats on what's in the database

    CALL apoc.meta.stats()
    YIELD nodeCount, relCount, labels, relTypesCount
    RETURN nodeCount, relCount, labels, relTypesCount;

Get a few Page -> Keyword relationships to see what they look like

    MATCH (n)-[:KEYUSED]->(s) RETURN n,s limit 50
    
![page to keyword relationships in neo4j](/Pages-to-keywords.png "Page to Keyword Relationships")

