CXX = g++
CXXFLAGS = -std=c++17 -Wall -O2 -I../FastUDP
LDFLAGS = -lpthread

SRC = Client.cpp ../FastUDP/FClient.cpp
TARGET = app
BENCHMARK_SRC = Benchmark.cpp ../FastUDP/FClient.cpp
BENCHMARK_TARGET = bench

all: $(TARGET)

$(TARGET): $(SRC)
	$(CXX) $(CXXFLAGS) $(SRC) -o $(TARGET) $(LDFLAGS)

$(BENCHMARK_TARGET): $(BENCHMARK_SRC)
	$(CXX) $(CXXFLAGS) $(BENCHMARK_SRC) -o $(BENCHMARK_TARGET) $(LDFLAGS)

run: $(TARGET)
	./$(TARGET)

benchmark: $(BENCHMARK_TARGET)
	./$(BENCHMARK_TARGET)

clean:
	rm -f $(TARGET) $(BENCHMARK_TARGET)
