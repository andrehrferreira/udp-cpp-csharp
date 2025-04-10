CXX = g++
CXXFLAGS = -std=c++17 -Wall -O2 -I../FastUDP
LDFLAGS = -lpthread

SRC = Client.cpp ../FastUDP/FClient.cpp
TARGET = app

all: $(TARGET)

$(TARGET): $(SRC)
	$(CXX) $(CXXFLAGS) $(SRC) -o $(TARGET) $(LDFLAGS)

run: $(TARGET)
	./$(TARGET)

clean:
	rm -f $(TARGET)
